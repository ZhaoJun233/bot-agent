using BotAgent.Services.Plugins;
using BotAgent.Services.Memory;
using BotAgent.Services.Evaluation;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Links;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;
using BotAgent.Services.Panel;
using BotAgent.Services.Ports;
using BotAgent.Services.Ops;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Poke;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Resilience;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using System.Collections.Concurrent;
using System.Text;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    private UnifiedMemoryTurn? BeginMemoryTurn()
    {
        BotAgent.Domain.Messaging.PlatformInboundEnvelope? memoryEnvelope = null;
        if (PlatformSourceExecution.Current is { } memorySource) _memoryInbound.TryGetValue(memorySource, out memoryEnvelope);
        return _unifiedMemory is null ? null : UnifiedMemoryTurn.Begin(_unifiedMemory, memoryEnvelope);
    }

    private void ObserveModelFailure(BotConversation conversation, TurnInputs turn, Exception ex)
    {
        SetThinking(conversation, false);
        // 连异常类型和第一帧调用堆栈一起记：只记 Message 时，“空引用”这种根本看不出在哪（踩过）
        if (MemoryUseScope.HasMemory) _hooks.Log($"模型请求失败: {ex.GetType().Name}");
        else _hooks.Log($"模型请求失败: {ex.GetType().Name} {ex.Message}" +
                (ex.StackTrace is { Length: > 0 } stack
                    ? "　@ " + stack.Split('\n').FirstOrDefault(l => l.Contains("BotAgentHost"))?.Trim()
                    : string.Empty));
        // P1 观测：模型这一路出事了（超时/连不上/上游 5xx）→ ModelTimeout（**只降级不升级**）。
        // 注意与“模型选择沉默”分开：那是它主动不说话，这是它没能说上话。
        _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ModelTimeout);
        _research.KeepNote(conversation.SourceKey, turn.SearchText, "模型请求失败");
    }

    private async Task GenerateReplyAsync(BotConversation conversation, long? triggerMessageId, bool proactive = false, CancellationToken ct = default, IPacedReplyAttempt? pacing = null)
    {
        using var deliveryBinding = BindReplyDelivery(conversation, triggerMessageId, proactive);
        var explicitPolicyRequest = pacing?.IsExplicit ?? (triggerMessageId is long mentionId &&
            conversation.Messages.FirstOrDefault(m => m.QqMessageId == mentionId)?.MentionedBot == true);
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true && !explicitPolicyRequest)
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "protocol_backoff");
            return;
        }

        var policyContext = new BotAgent.Domain.Plugins.ReplyPolicyContext(conversation.SourceKey, proactive, explicitPolicyRequest);
        if (_replyPolicies?.BlockReason(policyContext) is { } blockReason)
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Participation, "silent", reasonCode: blockReason);
            return;
        }

        // 配置快照（V3 §5.3）：这一轮从开始到结束一律读它 —— 面板热更新只影响后续处理，
        // 不会让"请求已经在路上"的这一轮中途换开关（能力闸门另有 caps 快照）。
        // 频率门（语音/表情/戳的冷却）仍读实时值：那是限速，不是授权。
        var snapshot = pacing?.Snapshot ?? _settings.Snapshot();
        var textOnly = pacing?.IsSoftDraft == true;
        var caps = _approvals.Capabilities;
        var platformPolicy = PlatformSourceExecution.Current is { } source
            ? _platformPolicies.Resolve(source.Context)
            : _platformPolicies.ResolveForChannel(Channels.ChannelOf(conversation.SourceKey));
        if (!platformPolicy.Registered || !platformPolicy.Connected || !platformPolicy.Enabled || !platformPolicy.ChatEnabled)
        {
            _traces.Node(conversation.SourceKey, TurnNodeKind.Outbound, "blocked", reasonCode: "platform_chat_disabled");
            return;
        }

        using var memoryTurn = BeginMemoryTurn();
        var turn = await _turnContext.BuildAsync(conversation, snapshot, caps, platformPolicy, proactive, pacing);
        if (turn is null)
        {
            return;
        }
        if (MemoryUseScope.HasMemory)
        {
            caps = BotAgent.Domain.Permissions.ChatCapabilitySet.FromSwitches(false, false, false, false, false, null);
            turn = turn with { Caps = caps, StickerChoices = new() };
        }
        _traces.Node(conversation.SourceKey, TurnNodeKind.Context, "ok", count: turn.Context.Count);

        CompletionResult result;

        // 自然对话驱动 agent（默认关）：够资格才在提示词里出现 action=agent 这条路（见 ReplyPipeline.NaturalAgent.cs）。
        var naturalTrigger = NaturalAgentTrigger(conversation, triggerMessageId, proactive, snapshot.EnableNaturalAgent && !textOnly, pacing?.IsExplicit);
        var naturalAgent = !MemoryUseScope.HasMemory && naturalTrigger is not null;

        try
        {
            // 批次 E：模型步骤交给有限步进循环（默认 1 步 = 只调一次，与改造前逐字一致）。
            var outcome = await _turnLoop.RunAsync(
                conversation.SourceKey, turn, caps, proactive, MemoryUseScope.HasMemory ? 1 : snapshot.MaxAgentSteps,
                result2 => _inlineTools.RunAsync(conversation, turn.Snapshot, caps, result2, turn.PlatformPolicy, ct),
                naturalAgent ? NaturalAgentPrompt.Hint : null, ct,
                step => _usage?.RecordUsage(conversation.SourceKey, step.PromptTokens, step.CompletionTokens), pacing);
            result = outcome.Result;
        }
        catch (ReplyPacingInterruptedException)
        {
            SetThinking(conversation, false);
            throw; // Scheduler yield/retry is not a failed model request.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || !_replyTools.IsCurrent)
        {
            SetThinking(conversation, false);
            return; // Source retirement/shutdown is not a provider failure.
        }
        catch (Exception ex)
        {
            ObserveModelFailure(conversation, turn, ex);
            return;
        }

        // Model-supplied metadata may repeat a fact; keep diagnostics shape-only.
        if (MemoryUseScope.HasMemory) result = result with { ReasonCode = "memory-restricted", ToolId = null };
        var elapsed = (EngineClock.LocalDateTime - turn.Started).TotalMilliseconds;
        Volatile.Write(ref _lastGenerationMs, (long)elapsed);
        SetThinking(conversation, false);

        if (!textOnly && !MemoryUseScope.HasMemory && await TryDispatchNaturalAgentAsync(conversation, naturalTrigger, result, elapsed))
        {
            return;
        }

        // 发言适合度门槛：以前只写在提示词里、代码不执行；现在真正生效。
        // 模型未按 JSON 输出（Suitability == null）时按普通文本回复处理，不拦截。
        // 例外：**这一轮带着刚查到的资料**时不受门槛限制 —— 模型自评“现在插嘴合适吗”时
        // 往往给低分（它只是回来汇报查到的东西，不是要插话），结果就是“查了半天啥也不说”。
        // 查都查了，就得让它说出来；真不想说（空回复）时下面会把资料留给下一轮。
        // 情绪介入（这一步才是“人性化陪伴”的关键）：先看它读到的气氛，再决定门槛 ——
        //   • 吵架/对线：不插嘴（门槛抬到 60，即“非说不可”才说）
        //   • 低落/求助/孤独：更愿意轻声接一句（门槛降 10），但禁掉表情包/语音/戳（人家难过时发图很尬）
        //   • 生气/吐槽：照常，但也不发表情包（容易像在嘲笑）
        var vibe = result.Vibe ?? "中性";
        var baseThreshold = Math.Clamp(turn.Snapshot.SuitabilityThreshold, 0, 100);

        // 这一轮是不是“人家在跟你说话”：触发那条 @ 了你，或引用了你发的那句话。
        // 两个用途：① 自评再低也接（被点名不应该沉默）；② 引用优先挂给点名的人。
        var directAddress = pacing?.IsExplicit ?? (triggerMessageId is long directTriggerId &&
                            conversation.Messages.FirstOrDefault(m => m.QqMessageId == directTriggerId)?.DirectToBot == true);
        var threshold = VibeRules.VibeAdjustedThreshold(vibe, baseThreshold, out var vibeReason);
        var soberMood = vibe is "低落" or "求助" or "吵架" or "生气" or "吐槽";

        if (!MemoryUseScope.HasMemory && vibe != "中性" && result.VibeNote is { Length: > 0 })
        {
            _hooks.Log($"[Vibe] {conversation.Name}：读到「{vibe}」——{result.VibeNote}" +
                    (vibeReason.Length > 0 ? $"（{vibeReason}）" : string.Empty));
        }

        // 不管这轮说不说话，读到的气氛都记下来：沉默也是一种回应，下一轮要接得上
        if (!MemoryUseScope.HasMemory && AllowsFeature(PluginGate.VibesCapability))
            _vibes.Remember(conversation.SourceKey, vibe, result.VibeNote);

        if (result.Suitability is int score && score < threshold)
        {
            // 被点名（@ 你 / 引用你的话）就该答，门槛不适用于这种轮次：
            // 管理员反馈“直接跟我说话它也不理”—— 自评低说明模型“不想插嘴”，但人家就是在问它，
            // 沉默在群里看着就是坏了（而且连珠炮场景下会连着好几条都不理）。
            // ⚠ 但“有人在对线”那一档是刻意设的规矩（不站队/不评理/不添柴）：哪怕 @ 你评理也不接。
            if (directAddress && vibe != "吵架")
            {
                _hooks.Log($"自评 {score} < 阈值 {threshold}，但这条是直接跟机器人说话（@ 你/引用了你的话）→ 照样接");
            }
            else if (turn.SearchText is null)
            {
                  _hooks.Log($"适合度不足 → 沉默（评分 {score} < 阈值 {threshold}" +
                          (vibe != "中性" ? $"，气氛 {vibe}" : string.Empty) + $"，{elapsed:F0}ms）: {conversation.Name}");
                  // P1 观测：这一轮**也是它选择不说**（只是理由是自评太低）→ 同样喂 Silent。
                  // 为什么不能只在那条“空回复”分支喂：现实里绝大多数沉默都是走这一支（自评 < 阈值），
                  // 漏掉它，状态机就永远看不到“连续静默”，V3 §7.3 的「静默 → 退场」也就形同虚设。
                  _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.Silent);
                  return;
            }
            else
            {
                _hooks.Log($"适合度不足（{score} < {threshold}）但本轮带着刚查到的资料 → 照样说");
            }
        }

        var sticker = textOnly || MemoryUseScope.HasMemory ? null : await _actions.QueueTurnActions(
            conversation, turn.Snapshot, turn.Caps, turn.PlatformPolicy, result, directAddress);

        var reply = (result.Reply ?? string.Empty).Trim();

        // 模型想“用语音说这句”（speak）。真正的发送在下边（要等 isGroup/targetId），
        // 这里先把文本取出来：它得参与“沉默判定”与消息落库，否则“只发语音不说话”会被当成空回复。
        var voiceText = textOnly ? string.Empty : (result.Speak ?? string.Empty).Trim();

        // 断句归模型（管理员 2026-09-21）：它可以在 speak 里用 `|` 或换行把话切成几段，每段 = 一条语音条（最多 3 条）。
        // 为什么不让程序切：中文的停顿是语气的一部分（“你是不是傻，我可没这么说” vs “你是不是傻 | 我可没这么说”
        // 听着是两句话 ✗），这种事模型比正则懂。代码只留技术性限制（条数/字数）。
        var voiceParts = voiceText
            .Split(new[] { '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)   // 模型有时把同一句写两遍（A | A）→ 只发一条
            .Take(3)
            .ToList();
        if (voiceParts.Count == 0 && voiceText.Length > 0)
        {
            voiceParts.Add(voiceText);
        }

        // 模型可以顺手写一句“我现在的心情”——存下来，下一轮提示词里带上（空/太长会被忽略）
        if (!MemoryUseScope.HasMemory && _mood.SetText(result.Mood, EngineClock.Now))
        {
            _hooks.Log($"心情变成：{_mood.Describe(EngineClock.Now)}");
        }

        // 模型可以“只戳不说话”：这样也算有动作，不算沉默
        var pokeTarget = textOnly ? null : result.PokeTargetId;

        if (await TryHandleTurnShortcutsAsync(
                conversation, turn.Snapshot, turn.Caps, result, reply, sticker, pokeTarget, voiceText, triggerMessageId, turn.SearchText, elapsed, allowEffects: !textOnly))
        {
            return;   // 这一轮已经到此为止（开了待批单 / 问了问题 / 就是不说）
        }

        var (targetStopped, replyTo) = _quotes.ResolveTarget(conversation, result, turn, triggerMessageId, reply, directAddress, elapsed);
        if (targetStopped)
        {
            return;
        }

        var (isGroup, targetId) = conversation.Target;

        var voiceReport = await _delivery.TryVoiceAsync(
            conversation, turn.Snapshot, turn.Caps, turn.PlatformPolicy, voiceParts, voiceText, isGroup, targetId, result, directAddress);

        var report = await _delivery.SendAsync(
            conversation, turn.Context, triggerMessageId, turn.Caps, turn.PlatformPolicy, isGroup, targetId, replyTo,
            reply, voiceText, voiceParts, voiceReport, sticker, pokeTarget, elapsed, result, directAddress);
        pacing?.RecordDelivery(report);
    }


    /// <summary>
    /// 第 4 步里**不需要真的发东西**的那几条出口：开待批单（action=tool）、开待答问题（action=ask）、
    /// 以及“这一轮就是不说话”（空回复 / 上游空响应 / 自评低已经在上一步拦过）。
    /// 返回 true = 本轮到此为止。三条出口都**只借助既有链路**：服务端不执行任何工具、不授予任何权限。
    /// </summary>
    private async Task<bool> TryHandleTurnShortcutsAsync(
        BotConversation conversation,
        AppSettings snapshot,
        Domain.Permissions.ChatCapabilitySet caps,
        CompletionResult result,
        string reply,
        StickerRecord? sticker,
        long? pokeTarget,
        string voiceText,
        long? triggerMessageId,
        string? searchText,
        double elapsed,
        bool allowEffects = true)
    {
        // P3 审批触发点（V3 §9.4，**默认关**）：模型说“我想调工具”时，服务端**绝不上手执行**，
        // 只可能开一张待批单并公告到群里（模型输出不构成授权）。开不出单 → 照旧安全静默。
        if (allowEffects && !MemoryUseScope.HasMemory && snapshot.EnableApprovals
            && result.Action == Domain.Reply.ReplyAction.Tool
            && reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0)
        {
            await ReplyCommitBoundary.BeforeEffectAsync();
            if (await _approvals.OpenApprovalForTool(conversation, result.ToolId, triggerMessageId, caps))
            {
                _research.KeepNote(conversation.SourceKey, searchText, "等待审批");
                return true;
            }
        }

        // P2 提问路径（V3 §8.1 的 action=ask，**默认关**）：模型想问就问，但**提问不是权限** ——
        // 服务端只是把问题包一层自己的文案（一次性编号 + 有效期）发到当前会话；
        // 谁答一声都算答完，回答也只是一条普通消息，不触发任何动作。
        if (allowEffects && !MemoryUseScope.HasMemory && snapshot.EnableQuestions
            && result.Action == Domain.Reply.ReplyAction.Ask
            && reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0)
        {
            await ReplyCommitBoundary.BeforeEffectAsync();
            if (await _approvals.OpenQuestionForAsk(conversation, result.QuestionText, triggerMessageId, caps))
            {
                _research.KeepNote(conversation.SourceKey, searchText, "等待回答");
                return true;
            }
        }

        if (reply.Length == 0 && sticker is null && pokeTarget is null && voiceText.Length == 0)
        {
            // “上游把回复吞了”（200 但没 choices，已重试一次）与“模型自己决定不说话”不是一回事：
            // 以前两种都写成“模型选择沉默”，主人根本看不出是网关出事了（22:09 那条就是这样）。
            var why = result.UpstreamEmpty
                ? "上游空响应"
                : result.Suitability is int s2 ? $"自评 {s2}" : "空回复";
            _hooks.Log(result.UpstreamEmpty
                ? $"本轮没拿到模型输出（上游连续两次空响应，{elapsed:F0}ms，已重试）: {conversation.Name}"
                : $"模型选择沉默（{why}，{elapsed:F0}ms）: {conversation.Name}");
            // P2（结构化运行记录，V3 §12.2）：静默也要能一眼看出是哪一类 ——
            // 「模型自己决定不说」和「控制字段非法/空回复被降级」在排查时是两回事。
            // 只写结构化字段（不写正文、不写提示词）。
            _hooks.Log($"[决策] outcome=suppressed action={result.Action} reason={result.ReasonCode ?? "unknown"}"
                    + (result.Malformed ? " malformed=true（按安全默认降级）" : string.Empty)
                    + $": {conversation.Name}");
            // P1 观测：这一轮它**自己决定不说**（或被安全降级成不说）→ Silent。
            // V3 §7.3 那一列写着「连续静默 → active/exiting 退场」，所以这条不喂进去，状态机就永远
            // 看不到“它刚选择了沉默”这个事实 —— 之前的 Silent 只在“服务端拦下”（总开关关/不在白名单）。
            _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.Silent);
            _research.KeepNote(conversation.SourceKey, searchText, why);
            return true;
        }
        return false;
    }
}
