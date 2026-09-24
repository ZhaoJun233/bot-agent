using BotAgent.Domain.Permissions;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Permissions;

/// <summary>
/// 能力闸门 + 人工审批 + 待答问题（V3 §8.1 / §9.2 / §9.4 的服务端那一半）。
///
/// 这个用例里有三样互相咬合的状态，一起搬才不会拆出"半更新"的窗口：
///   • **策略快照**（<see cref="ChatCapabilitySet" />）：惰性构建，设置变了整体换一份；
///     版本号只在**能力真的变了**时前进（面板保存一次无关设置不该把在途审批单弄失效）；
///   • **审批台账**（<see cref="ApprovalStore" />）：纯内存、重启即清空（待批单子自动作废，符合短有效期意图）；
///   • **单轮工具预算**（<see cref="ToolCallBudget" />）：收到新的用户消息时归零，同一轮里放行一次记一笔。
///
/// 判决顺序全在 <see cref="ApprovalFlow" />（纯逻辑、可测）里；这里只负责：把身份/角色/时间交给它、
/// 必要时过一次闸门、把回执发出去、写结构化日志。**本类不执行任何真实系统能力** ——
/// 审批通过也只跑 <see cref="ApprovalFlow.FixedToolId" /> 这个固定假工具（V3 §9.4 明文禁止）。
///
/// 宿主能力从 <see cref="ApprovalHooks" /> 注入（方向：用例 → 宿主）。
/// </summary>
public sealed class ApprovalUseCase
{
    private readonly SettingsBox _box;
    private readonly ApprovalHooks _hooks;

    private readonly ApprovalStore _approvals = new();
    private readonly ToolCallBudget _toolBudget = new();

    private ChatCapabilitySet? _capabilities;
    private int _capabilityVersion = 1;
    private string _capabilityFingerprint = string.Empty;

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public ApprovalUseCase(SettingsBox box, ApprovalHooks hooks)
    {
        _box = box;
        _hooks = hooks;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>当前策略快照（惰性建一次；之后只在设置变更时由 <see cref="RebuildPolicy" /> 换）。</summary>
    public ChatCapabilitySet Capabilities
    {
        get
        {
            if (_capabilities is null)
            {
                RebuildPolicy();
            }

            return _capabilities!;
        }
    }

    /// <summary>审批人名单（面板里点名的人；留空 = 只认群里的 owner/admin）。</summary>
    public HashSet<string> Approvers()
        // 归一化交给 ApprovalStore（裸 QQ 号 → user:<号>，与审批时传入的身份同一格式）
        => ApprovalStore.NormalizeApprovers(_settings.ApprovalApprovers);

    /// <summary>
    /// 设置变了 → 重建策略快照（在 <c>ApplyRuntimeSettings</c> 里调用）。
    /// 先按当前版本算一次，指纹变了才升版本再算一次 —— 版本只在**能力真的变了**时前进。
    /// </summary>
    public void RebuildPolicy()
    {
        var candidate = Build();
        if (_capabilityFingerprint.Length > 0 && candidate.PolicyFingerprint != _capabilityFingerprint)
        {
            _capabilityVersion++;
            candidate = Build();
            _hooks.Log($"[能力] 策略已变更 → 版本 v{_capabilityVersion}（{candidate.Describe()}）");
        }

        _capabilityFingerprint = candidate.PolicyFingerprint;
        _capabilities = candidate;
    }

    private ChatCapabilitySet Build()
        => ChatCapabilitySet.FromSwitches(
            enableWebSearch: _settings.EnableWebSearch,
            enableMusic: _settings.EnableMusic,
            enableVoice: _settings.EnableVoice,
            enableStickers: _settings.EnableStickers,
            enablePoke: _settings.EnablePoke,
            scenario: _settings.ScenarioPreset,
            policyVersion: _capabilityVersion,
            approvalsEnabled: _settings.EnableApprovals,
            questionsEnabled: _settings.EnableQuestions);

    /// <summary>
    /// 判定一次聊天能力（V3 §9.2）：**默认关闭**、不在白名单就连协议端都不会被调用。
    /// 拒绝只写结构化原因码，不影响这一轮的其余部分。
    /// </summary>
    /// <param name="pinned">
    /// 本轮开始时固定的策略快照（V3 §5.3）。传 null 表示调用点不在某次处理里（例如面板自测），用当前策略。
    /// </param>
    public bool AllowCapability(
        BotConversation conversation,
        string toolId,
        string? untrustedHint,
        out string reason,
        ChatCapabilitySet? pinned = null)
    {
        var caps = pinned ?? Capabilities;
        var key = conversation.SourceKey;

        // 单次运行预算（V3 §9.2）：同一轮里每放行一次就记一笔，序号交给闸门判断；
        // 记账在**收到新的用户消息**时归零（见 HandleInbound），多轮工具补轮因此共享同一个预算。
        var decision = caps.Check(
            toolId, key, callIndex: _toolBudget.Peek(key), untrustedHint: untrustedHint);
        if (decision.Allow)
        {
            _toolBudget.Commit(key);
        }

        reason = decision.ReasonCode;
        if (!decision.Allow)
        {
            _hooks.Log($"[能力] 拒绝 {toolId}（{decision.ReasonCode}）: {conversation.Name}");
        }

        return decision.Allow;
    }

    /// <summary>收到新的用户消息 → 这个会话的单轮工具预算从 0 起算。</summary>
    public void ResetBudget(string sourceKey) => _toolBudget.Reset(sourceKey);

    /// <summary>会话被删 / 移出白名单 → 把它的台账痕迹一起清掉（内存台账要有界）。</summary>
    public void Forget(string sourceKey) => _toolBudget.Forget(sourceKey);

    /// <summary>
    /// 处理一条「同意 / 拒绝 编号」。返回 true = 这条消息被审批流程吃掉了（不再进模型）。
    ///
    /// 判决顺序全在 <see cref="ApprovalFlow.Handle" /> 里（纯逻辑、可测）：
    /// 未知编号 → 不动作；别的会话里批 → 不动作；过期 → 不动作；普通成员 → 不动作；
    /// 已经决定过 → 不动作；批准后**一次性消费**票据，重复消费拿不到票据。
    /// 这里只负责：把审批者身份（id + 消息事件里的角色）交给它、必要时过一次闸门、然后把回执发出去。
    /// </summary>
    public bool HandleCommand(QqChatMessage msg)
    {
        var command = ApprovalFlow.TryParseCommand(msg.Text);
        if (command is null)
        {
            return false;
        }

        var conversationKey = Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        var parsed = command.Value;

        var result = ApprovalFlow.Handle(
            _approvals,
            parsed,
            requesterId: "user:" + msg.UserId,
            requesterRole: msg.SenderRole,
            conversationKey: conversationKey,
            now: Clock.Now,
            currentPolicyVersion: Capabilities.Policy.PolicyVersion);

        if (!result.Handled)
        {
            // 台账不可用之类的内部原因：不吞消息（让它走普通链路，免得表现为"机器人不理人"）
            _hooks.Log($"[审批] 未处理（{result.ReasonCode}）：{Channels.Describe(conversationKey)}");
            return false;
        }

        var who = _hooks.Mask("user:" + msg.UserId, conversationKey);
        _hooks.Log($"[审批] {parsed.Kind} {parsed.RequestId} by {who} → {result.ReasonCode}"
                   + (result.ReasonCode == "not_an_approver" ? "（不是名单内的人，也不是群主/管理员）" : string.Empty)
                   + $"：{Channels.Describe(conversationKey)}");

        if (result.ShouldExecute && result.Ticket is { } ticket)
        {
            // 执行前**再过一次闸门**（拿着票据）：闸门才是"能不能执行"的唯一判据。
            // 高风险类别在闸门里任何审批都放不开 —— 这一层不依赖审批流程写得对不对。
            var gate = Capabilities.Check(ApprovalFlow.FixedToolId, conversationKey, ticket: ticket);
            _hooks.Log($"[审批] 执行前闸门：{gate.Describe()}（{ApprovalFlow.FixedToolId}）");
            if (!gate.Allow)
            {
                _hooks.Log($"[审批] 闸门拒绝执行（{gate.ReasonCode}）→ 不执行：{Channels.Describe(conversationKey)}");
                return true;
            }

            RunFixedDemoTool(msg, conversationKey, parsed.RequestId, result.Reply);
            return true;
        }

        if (result.Reply is { Length: > 0 } reply)
        {
            _ = _hooks.SendApprovalReplyAsync(msg, reply);
        }

        return true;
    }

    /// <summary>
    /// 「执行」固定假工具 <see cref="ApprovalFlow.FixedToolId" />。
    /// **它真的什么都不做**：只记一行日志 + 回一句说明 —— 审批这条链路**没有**接文件 / shell /
    /// 进程 / 远程桥能力（V3 §9.4 明文禁止拿它宣称真实系统具备这些能力）。
    /// </summary>
    private void RunFixedDemoTool(QqChatMessage msg, string conversationKey, string requestId, string? reply)
    {
        _hooks.Log($"[审批] 执行固定假工具 {ApprovalFlow.FixedToolId}"
                   + $"（编号 {requestId}，**无真实副作用**）：{Channels.Describe(conversationKey)}");

        if (reply is { Length: > 0 })
        {
            _ = _hooks.SendApprovalReplyAsync(msg, reply);
        }
    }

    /// <summary>
    /// 模型想问问题时的服务端处理（V3 §8.1 的 <c>action=ask</c>，**默认关**）：
    /// 过一次能力闸门 → 开一张**待答**单（一次性编号 + 短有效期 + 会话绑定）→ 把服务端包好的提问发出去。
    /// 返回 true = 本轮到此为止（问题已经问出去了）。
    ///
    /// ⚠ 提问**不授予任何权限**：台账里那张单子只是"这个会话还有一个没人答的问题"，
    /// 回答也只是一条普通消息 —— 既不触发动作，也不改变任何策略。
    /// </summary>
    public async Task<bool> OpenQuestionForAsk(
        BotConversation conversation, string? questionText, long? triggerMessageId, ChatCapabilitySet caps)
    {
        var reason = string.Empty;
        var policyVersion = caps.Policy.PolicyVersion;

        // 走同一条闸门（"往当前会话发额外消息"这类动作都由它统一判定）
        if (!AllowCapability(conversation, ApprovalFlow.QuestionToolId, null, out reason, pinned: caps))
        {
            _hooks.Log($"[提问] 闸门拒绝（{reason}）→ 不提问：{conversation.Name}");
            return false;
        }

        var created = ApprovalFlow.CreateForModelQuestion(
            _approvals,
            questionText: questionText,
            conversationKey: conversation.SourceKey,
            requesterFingerprint: QuestionRequesterFingerprint(conversation, triggerMessageId),
            now: Clock.Now,
            newRequestId: () => ApprovalFlow.NewRequestId(
                upper => System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper)),
            policyVersion: policyVersion);

        if (!created.Created)
        {
            // 空问题 / 同一会话已经挂着待答的问题 → 什么都不做（照旧安全静默）
            _hooks.Log($"[提问] 没有开单（{created.ReasonCode}）：{conversation.Name}");
            return false;
        }

        _hooks.Log($"[提问] 新建待答问题 {created.Request?.RequestId}"
                   + $"（v{policyVersion}，{ApprovalFlow.TtlSeconds}s 有效）：{conversation.Name}");

        if (created.Announcement is { Length: > 0 } announcement)
        {
            await _hooks.SendPlainAsync(conversation, announcement);
        }

        return true;
    }

    /// <summary>
    /// 有人应了一声就把这条待答问题标记为「已答」（一次性；跨会话/过期/已答都不认）。
    /// 只在 <c>EnableQuestions</c> 打开时才跑，且**失败不做任何动作**。
    /// </summary>
    public void MarkQuestionAnswered(BotConversation conversation, QqChatMessage msg)
    {
        var pending = _approvals.FindPending(
            conversation.SourceKey, ApprovalFlow.QuestionToolId, Clock.Now);
        if (pending is null)
        {
            return;
        }

        // 记下"谁答的"（身份摘要，日志里按脱敏开关显示）：V3 §8.1 要求待答问题**有身份**。
        // 产品口径仍是"谁答都算答完"（群聊里不 @ 也应一声是常态），但台账与运行记录里要能看出是谁。
        var answeredBy = "user:" + msg.UserId;
        var outcome = _approvals.MarkAnswered(
            pending.RequestId, conversation.SourceKey, Clock.Now, answeredBy);
        _hooks.Log(outcome.Ok
            ? $"[提问] {pending.RequestId} 已被回答（一次性消费完，by {_hooks.Mask(answeredBy, conversation.SourceKey)}）：{conversation.Name}"
            : $"[提问] {pending.RequestId} 没有标记为已答（{outcome.ReasonCode}）：{conversation.Name}");
    }

    /// <summary>
    /// 提问单上的"请求者指纹"：优先记**是谁**（user:&lt;uid&gt;），再附上触发消息 id 便于回溯；
    /// 老实现只记 msg:&lt;id&gt; —— 那是"哪条消息"，不是"哪个人"（V3 §8.1 要求有身份）。
    /// 只进内存台账与结构化日志，不外发。
    /// </summary>
    private static string QuestionRequesterFingerprint(BotConversation conversation, long? triggerMessageId)
    {
        var uid = triggerMessageId is long qid
            ? conversation.Messages.FirstOrDefault(m => m.QqMessageId == qid)?.SenderId
            : null;
        var who = uid is long id ? "user:" + id : "user:unknown";
        return triggerMessageId is { } mid ? $"{who}#msg:{mid}" : who;
    }

    /// <summary>
    /// 模型说"我想调工具"时的服务端处理（V3 §9.4 的触发点，**默认关**）：
    /// **不执行任何东西**，只可能开一张待批单并把请求公告发到群里。
    /// 返回 true = 本轮到此为止（已经公告过了）。
    /// </summary>
    public async Task<bool> OpenApprovalForTool(
        BotConversation conversation, string? toolId, long? triggerMessageId, ChatCapabilitySet caps)
    {
        var (isGroup, _) = conversation.Target;
        var policyVersion = caps.Policy.PolicyVersion;
        var created = ApprovalFlow.CreateForModelTool(
            _approvals,
            requestedToolId: toolId,
            conversationKey: conversation.SourceKey,
            requesterFingerprint: triggerMessageId is { } id ? "msg:" + id : "msg:unknown",
            configuredApprovers: Approvers(),
            allowGroupAdmins: true,
            isGroup: isGroup,
            now: Clock.Now,
            newRequestId: () => ApprovalFlow.NewRequestId(
                upper => System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper)),
            policyVersion: policyVersion);

        if (!created.Created)
        {
            // 模型点了服务端没登记的工具、或同一件事已经挂着待批单 → 什么都不做（照旧安全静默）
            _hooks.Log($"[审批] 没有开单（{created.ReasonCode}"
                       + (toolId is { Length: > 0 } asked ? $"，模型想调 {asked}" : string.Empty)
                       + $"）：{conversation.Name}");
            return false;
        }

        _hooks.Log($"[审批] 新建待批单 {created.Request?.RequestId}（{ApprovalFlow.FixedToolId}"
                   + $"，v{policyVersion}，"
                   + $"{ApprovalFlow.TtlSeconds}s 有效）：{conversation.Name}");

        if (created.Announcement is { Length: > 0 } announcement)
        {
            await _hooks.SendPlainAsync(conversation, announcement);
        }

        return true;
    }

    /// <summary>
    /// 审批台账周期清理（V3 §9.4：台账要有界）。跟着静默兜底的节奏走，顺手加个 10 分钟节流 ——
    /// 它只删已结束（过期/拒绝/取消/已答）的单子，不会动还在等待决定的那张。
    /// </summary>
    public void PruneIfDue(DateTimeOffset now)
    {
        if (now - _lastPrune <= TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastPrune = now;
        var pruned = _approvals.Prune(now);
        if (pruned > 0)
        {
            _hooks.Log($"[审批] 台账清理：移除 {pruned} 张已结束的单子");
        }
    }
}

/// <summary>
/// 审批用例要用到的宿主能力（由 BotAgentHost 提供实现）。
/// 全是回调而不是接口实现，是为了让宿主那边**一个方法都不新增**：接线只写在构造函数的参数里。
/// </summary>
/// <param name="Log">普通运行日志（写文件 + 推面板）。</param>
/// <param name="Mask">显示层脱敏（面板/日志里不出现完整 QQ 号）。</param>
/// <param name="SendPlainAsync">往当前会话发一条纯文本（服务端自己包好的公告/提问）。</param>
/// <param name="SendApprovalReplyAsync">把审批回执发回原会话（走带节奏的发送链路）。</param>
public readonly record struct ApprovalHooks(
    Action<string> Log,
    Func<string, string?, string> Mask,
    Func<BotConversation, string, Task> SendPlainAsync,
    Func<QqChatMessage, string, Task> SendApprovalReplyAsync);
