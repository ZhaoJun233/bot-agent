using BotAgent.Engine.Runtime;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Tools;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Qq;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BotAgent.Services.Tools;

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
    private readonly ISettingsBox _box;
    private readonly ApprovalHooks _hooks;
    private readonly SessionPolicyLedger _sessionPolicies;
    private readonly TurnTraceStore _traces;
    private readonly IAuditChain? _audit;
    private readonly Func<ChatToolCatalog> _catalog;
    private readonly IAccessRoleResolver? _accessRoles;
    private readonly Func<string, PlatformContext?, Services.Platforms.EffectiveConversationSettings?>? _resolveConversation;
    private readonly Func<string, bool>? _pluginAllowsTool;
    private readonly object _policySync = new();

    private readonly ApprovalStore _approvals = new();
    private readonly PendingToolInvocationStore _pendingTools = new();
    private readonly ConcurrentDictionary<string, PlatformSourceLease> _sources = new(StringComparer.Ordinal);
    private readonly ToolCallBudget _toolBudget = new();
    private Func<QqChatMessage, bool>? _hasPendingRuntimeTaskApproval;
    private Func<QqChatMessage, ApprovalCommand, bool>? _handleRuntimeTaskApproval;
    private Action<DateTimeOffset>? _onPeriodicSweep;

    private ChatCapabilitySet? _capabilities;
    private int _capabilityVersion = 1;
    private string _capabilityFingerprint = string.Empty;

    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public ApprovalUseCase(ISettingsBox box, ApprovalHooks hooks, SessionPolicyLedger sessionPolicies, TurnTraceStore traces,
        IAuditChain? audit = null, Func<ChatToolCatalog>? catalog = null, IAccessRoleResolver? accessRoles = null,
        Func<string, PlatformContext?, Services.Platforms.EffectiveConversationSettings?>? resolveConversation = null,
        Func<string, bool>? pluginAllowsTool = null)
    {
        _box = box;
        _hooks = hooks;
        _sessionPolicies = sessionPolicies;
        _traces = traces;
        _audit = audit;
        _catalog = catalog ?? (() => ChatToolCatalog.Builtin);
        _accessRoles = accessRoles;
        _resolveConversation = resolveConversation;
        _pluginAllowsTool = pluginAllowsTool;
    }

    public void BindRuntimeTaskApprovals(
        Func<QqChatMessage, bool> hasPending,
        Func<QqChatMessage, ApprovalCommand, bool> handle)
    {
        _hasPendingRuntimeTaskApproval = hasPending ?? throw new ArgumentNullException(nameof(hasPending));
        _handleRuntimeTaskApproval = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    public void BindPeriodicSweep(Action<DateTimeOffset> sweep)
        => _onPeriodicSweep = sweep ?? throw new ArgumentNullException(nameof(sweep));

    public bool HasPendingRuntimeTaskApproval(QqChatMessage msg)
        => _hasPendingRuntimeTaskApproval?.Invoke(msg) == true;

    private AppSettings _settings => _box.Current;

    /// <summary>Refreshes settings and adopted generations together; unchanged content keeps the same version.</summary>
    public ChatCapabilitySet Capabilities => RefreshPolicy();

    /// <summary>按会话档案有效开关投影本轮能力白名单，保留既有的版本号、场景预设、工具目录与永久禁止类别。</summary>
    public ChatCapabilitySet CapabilitiesFor(AppSettings settings, Services.Platforms.EffectiveConversationSettings? conversation)
    {
        var baseline = RefreshPolicy();
        if (conversation is null) return baseline;
        var web = conversation.FeatureEnabled("websearch") && conversation.AllowsReadOnlyTools;
        var music = conversation.FeatureEnabled("music");
        var voice = conversation.FeatureEnabled("voice");
        var stickers = conversation.FeatureEnabled("stickers");
        var poke = conversation.FeatureEnabled("poke");
        var approvals = settings.EnableApprovals && conversation.AllowsToolRequest;
        var questions = settings.EnableQuestions && conversation.AllowsAsk;
        var docsBaseline = baseline.Catalog.FindBinding("doc.read") is { IsReady: true };
        var digestBaseline = baseline.Catalog.FindBinding("chat.digest") is { IsReady: true };
        var knowledgeBaseline = baseline.Catalog.FindBinding("kb.search") is { IsReady: true };
        var schedulerBaseline = baseline.Catalog.FindBinding("reminder.list") is { IsReady: true };
        var todoBaseline = baseline.Catalog.FindBinding("todo.list") is { IsReady: true };
        var docs = docsBaseline && conversation.FeatureEnabled("documents") && conversation.AllowsReadOnlyTools;
        var digest = digestBaseline && conversation.FeatureEnabled("digest") && conversation.AllowsReadOnlyTools;
        var knowledge = knowledgeBaseline && conversation.FeatureEnabled("knowledge") && conversation.AllowsReadOnlyTools;
        var scheduler = schedulerBaseline && conversation.FeatureEnabled("scheduler") && conversation.AllowsReadOnlyTools;
        var personalTodo = todoBaseline && conversation.FeatureEnabled("personal_todo") && conversation.AllowsReadOnlyTools;
        var mcpReadBaseline = baseline.Catalog.Specs.Any(s => s.Origin == ToolOrigin.Mcp && s.ReadOnly);
        var mcpRead = conversation.AllowsReadOnlyTools;
        if (web == settings.EnableWebSearch && music == settings.EnableMusic && voice == settings.EnableVoice
            && stickers == settings.EnableStickers && poke == settings.EnablePoke
            && approvals == settings.EnableApprovals && questions == settings.EnableQuestions
            && docs == docsBaseline && digest == digestBaseline && knowledge == knowledgeBaseline
            && scheduler == schedulerBaseline && personalTodo == todoBaseline
            && (mcpRead || !mcpReadBaseline))
            return baseline;
        return ChatCapabilitySet.FromSwitches(
            enableWebSearch: web,
            enableMusic: music,
            enableVoice: voice,
            enableStickers: stickers,
            enablePoke: poke,
            scenario: settings.ScenarioPreset,
            policyVersion: baseline.Policy.PolicyVersion,
            approvalsEnabled: approvals,
            questionsEnabled: questions,
            catalog: baseline.Catalog,
            enableDocuments: docs,
            enableDigest: digest,
            enableKnowledge: knowledge,
            enableExternalMcpRead: mcpRead,
            enableScheduler: scheduler,
            enablePersonalTodo: personalTodo);
    }

    /// <summary>审批人名单（面板里点名的人；留空 = 只认群里的 owner/admin）。</summary>
    public HashSet<string> Approvers()
        // 归一化交给 ApprovalStore（裸 QQ 号 → user:<号>，与审批时传入的身份同一格式）
        => ApprovalStore.NormalizeApprovers(_settings.ApprovalApprovers);

    /// <summary>
    /// 面板要看的待批单（批次 I）：只给形状 —— 摘要在建单时已由台账脱敏，
    /// 会话 key 由面板那层再走一次显示层脱敏（<c>PanelDto.Mask</c>）。
    /// </summary>
    public IReadOnlyList<ApprovalRequest> PendingApprovals() => _approvals.Pending(EngineClock.Now);

    /// <summary>
    /// 设置变了 → 重建策略快照（在 <c>ApplyRuntimeSettings</c> 里调用）。
    /// 先按当前版本算一次，指纹变了才升版本再算一次 —— 版本只在**能力真的变了**时前进。
    /// </summary>
    public void RebuildPolicy() => _ = RefreshPolicy();

    private ChatCapabilitySet RefreshPolicy()
    {
        var settings = _settings;
        var catalog = _catalog();
        ChatCapabilitySet candidate;
        var changed = false;
        lock (_policySync)
        {
            candidate = Build(settings, catalog);
            var combinedFingerprint = candidate.PolicyFingerprint + BuildConversationProfilesFingerprint(settings);
            if (_capabilities is not null && combinedFingerprint == _capabilityFingerprint) return _capabilities;
            if (_capabilityFingerprint.Length > 0)
            {
                _capabilityVersion++;
                candidate = Build(settings, catalog);
                combinedFingerprint = candidate.PolicyFingerprint + BuildConversationProfilesFingerprint(settings);
                changed = true;
            }
            _capabilityFingerprint = combinedFingerprint;
            _capabilities = candidate;
        }
        if (changed) _hooks.Log($"[能力] 策略已变更 → 版本 v{candidate.Policy.PolicyVersion}（{candidate.Describe()}）");
        return candidate;
    }

    private static string BuildConversationProfilesFingerprint(AppSettings settings)
    {
        var profiles = settings.RuntimeConversationProfiles;
        if (profiles.IsDefaultOrEmpty) return string.Empty;
        var json = System.Text.Json.JsonSerializer.Serialize(profiles);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return "|conv[" + Convert.ToHexStringLower(hash) + "]";
    }

    private ChatCapabilitySet Build(AppSettings settings, ChatToolCatalog catalog)
        => ChatCapabilitySet.FromSwitches(
            enableWebSearch: settings.EnableWebSearch,
            enableMusic: settings.EnableMusic,
            enableVoice: settings.EnableVoice,
            enableStickers: settings.EnableStickers,
            enablePoke: settings.EnablePoke,
            scenario: settings.ScenarioPreset,
            policyVersion: _capabilityVersion,
            approvalsEnabled: settings.EnableApprovals,
            questionsEnabled: settings.EnableQuestions,
            catalog: catalog,
            enableDocuments: catalog.FindBinding("doc.read") is { IsReady: true },
            enableDigest: catalog.FindBinding("chat.digest") is { IsReady: true },
            enableKnowledge: catalog.FindBinding("kb.search") is { IsReady: true },
            enableScheduler: catalog.FindBinding("reminder.list") is { IsReady: true },
            enablePersonalTodo: catalog.FindBinding("todo.list") is { IsReady: true });

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

        // 批次 B（会话级权限元数据）：把“这条会话是哪个策略建立的”显式记下来。
        // **只记录**：判定口径一个字没改；只有在“策略指纹变了、这条会话的旧戳作废”时才留一行日志。
        if (_sessionPolicies.Stamp(SessionPolicyStamp.For(key, caps, EngineClock.Now)))
        {
            _hooks.Log($"[能力] 会话策略已重建 {Channels.Describe(key)} → v{caps.Policy.PolicyVersion}（指纹变了，旧戳作废）");
        }

        // 单次运行预算（V3 §9.2）：同一轮里每放行一次就记一笔，序号交给闸门判断；
        // 记账在**收到新的用户消息**时归零（见 HandleInbound），多轮工具补轮因此共享同一个预算。
        var decision = caps.Check(
            toolId, key, callIndex: _toolBudget.Peek(key), untrustedHint: untrustedHint);
        if (decision.Allow)
        {
            _toolBudget.Commit(key);
        }

        reason = decision.ReasonCode;
        _traces.Node(
            key,
            TurnNodeKind.Gate,
            decision.Allow ? "allowed" : "denied",
            toolId: toolId,
            reasonCode: decision.ReasonCode);
        if (!decision.Allow)
        {
            _hooks.Log($"[能力] 拒绝 {toolId}（{decision.ReasonCode}）: {conversation.Name}");
            if (_audit is not null)
            {
                var detail = System.Text.Json.JsonSerializer.Serialize(new
                {
                    result = "blocked",
                    tool = toolId,
                    reason = decision.ReasonCode
                });
                _audit.Append(new AuditEvent("gate_block", "agent-gate", key, detail, caps.Policy.PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return decision.Allow;
    }

    /// <summary>收到新的用户消息 → 这个会话的单轮工具预算从 0 起算。</summary>
    public void ResetBudget(string sourceKey) => _toolBudget.Reset(sourceKey);

    /// <summary>会话被删 / 移出白名单 → 把它的台账痕迹一起清掉（内存台账要有界）。</summary>
    public void Forget(string sourceKey)
    {
        _toolBudget.Forget(sourceKey);
        _sessionPolicies.Forget(sourceKey);
    }

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
        if (!msg.AllowsCommands) return false;
        var command = ApprovalFlow.TryParseCommand(msg.Text);
        if (command is null)
        {
            return false;
        }

        var conversationKey = Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);
        var parsed = command.Value;
        var currentRequest = _approvals.Get(parsed.RequestId, EngineClock.Now);
        if (currentRequest is null && _handleRuntimeTaskApproval?.Invoke(msg, parsed) == true)
        {
            return true;
        }

        _sources.TryGetValue(parsed.RequestId, out var originalSource);
        var incomingSource = PlatformSourceExecution.Current;
        if (originalSource is not null && (!originalSource.IsCurrent || incomingSource is null
            || incomingSource.Origin.Driver != originalSource.Origin.Driver || incomingSource.Lifetime != originalSource.Lifetime))
            return true;
        if (originalSource is not null)
        {
            var actualKey = originalSource.Owner.ConversationKey(msg, candidate => candidate == currentRequest?.ConversationKey);
            if (actualKey is null || actualKey != currentRequest?.ConversationKey) return true;
            conversationKey = actualKey;
        }
        // #117：从入口到执行之间平台源可能换代（重连会换 lifetime）。把真正要用的那一代抓成局部变量，
        // 执行前再核一次 IsCurrent —— 不能只靠入口那一次快照。
        var executingSource = originalSource ?? incomingSource;
        using var sourceScope = PlatformSourceExecution.Enter(executingSource);

        var requesterId = "user:" + msg.UserId;
        var requesterRole = Channels.IsOfficial(msg.Channel) ? null : msg.SenderRole;
        var isLiveApprover = false;
        if (_accessRoles is not null && currentRequest is not null)
        {
            var subject = InboundSubjectIdentity.Resolve(msg, executingSource?.Context);
            var nativeToken = "user:" + subject.NativeUserId;
            if (_accessRoles.TryResolveExplicitRole(subject, conversationKey, out var explicitRole))
            {
                if (explicitRole == WorkspaceRole.Guest)
                {
                    requesterId = nativeToken;
                    var guestWho = _hooks.Mask(requesterId, conversationKey);
                    _hooks.Log($"[审批] {parsed.Kind} {parsed.RequestId} by {guestWho} → not_an_approver（Guest 显式降权拦截）：{Channels.Describe(conversationKey)}");
                    _audit?.Append(new AuditEvent("approval_decision", guestWho, conversationKey,
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            requestId = parsed.RequestId,
                            action = parsed.Kind.ToString().ToLowerInvariant(),
                            reason = "not_an_approver",
                            shouldExecute = false
                        }),
                        Capabilities.Policy.PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    return true;
                }

                if (explicitRole is WorkspaceRole.GroupManager or WorkspaceRole.BotOwner)
                {
                    requesterId = nativeToken;
                    isLiveApprover = true;
                }
                else
                {
                    // 显式 RBAC 降权为 Member / TrustedMember 时，禁止回退使用 QQ 协议端 SenderRole 放行审批。
                    requesterId = nativeToken;
                    requesterRole = null;
                }
            }
            else if (currentRequest.Approvers.Contains(nativeToken))
            {
                requesterId = nativeToken;
            }
        }

        var result = ApprovalFlow.Handle(
            _approvals,
            parsed,
            requesterId: requesterId,
            requesterRole: requesterRole,
            conversationKey: conversationKey,
            now: EngineClock.Now,
            currentPolicyVersion: Capabilities.Policy.PolicyVersion,
            isLiveApprover: isLiveApprover);

        if (!result.Handled)
        {
            // 台账不可用之类的内部原因：不吞消息（让它走普通链路，免得表现为"机器人不理人"）
            _hooks.Log($"[审批] 未处理（{result.ReasonCode}）：{Channels.Describe(conversationKey)}");
            return false;
        }

        var who = _hooks.Mask(requesterId, conversationKey);
        _hooks.Log($"[审批] {parsed.Kind} {parsed.RequestId} by {who} → {result.ReasonCode}"
                   + (result.ReasonCode == "not_an_approver" ? "（没有本项目审批权限）" : string.Empty)
                   + $"：{Channels.Describe(conversationKey)}");

        if (_audit is not null && result.Handled)
        {
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                requestId = parsed.RequestId,
                action = parsed.Kind.ToString().ToLowerInvariant(),
                reason = result.ReasonCode,
                shouldExecute = result.ShouldExecute
            });
            _audit.Append(new AuditEvent("approval_decision", who, conversationKey, detail, Capabilities.Policy.PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (result.ShouldExecute && result.Ticket is { } ticket)
        {
            // 执行前**再过一次闸门**（拿着票据）：闸门才是"能不能执行"的唯一判据。
            // 高风险类别在闸门里任何审批都放不开 —— 这一层不依赖审批流程写得对不对。
            var toolId = ticket.ToolId;
            var liveCaps = ResolveLiveCapabilities(conversationKey, executingSource?.Context);
            var gate = liveCaps.Check(toolId, conversationKey, ticket: ticket);
            if (gate.Allow && !string.Equals(toolId, ApprovalFlow.FixedToolId, StringComparison.Ordinal)
                && _pluginAllowsTool is not null && !_pluginAllowsTool(toolId))
            {
                gate = new ToolDecision(false, "plugin_disabled", "插件或工具已被禁用");
            }
            _hooks.Log($"[审批] 执行前闸门：{gate.Describe()}（{toolId}）");
            if (!gate.Allow)
            {
                _pendingTools.Remove(parsed.RequestId);
                _hooks.Log($"[审批] 闸门拒绝执行（{gate.ReasonCode}）→ 不执行：{Channels.Describe(conversationKey)}");
                return true;
            }

            if (executingSource is { IsCurrent: false })
            {
                _pendingTools.Remove(parsed.RequestId);
                _hooks.Log($"[审批] 执行前平台源已换代（租约失效）→ 不执行：{Channels.Describe(conversationKey)}");
                return true;
            }

            if (string.Equals(toolId, ApprovalFlow.FixedToolId, StringComparison.Ordinal))
            {
                RunFixedDemoTool(msg, conversationKey, parsed.RequestId, result.Reply);
                return true;
            }

            if (!_pendingTools.TryTake(parsed.RequestId, out var pending)
                || !string.Equals(pending.ToolId, toolId, StringComparison.Ordinal)
                || !string.Equals(pending.ConversationKey, conversationKey, StringComparison.Ordinal))
            {
                _hooks.Log($"[审批] 真实工具参数快照缺失或失配（{parsed.RequestId}）→ 拒绝执行");
                if (result.Reply is { Length: > 0 })
                    _ = _hooks.SendApprovalReplyAsync(msg, "⚠️ 审批对应的工具调用已失效，未执行。");
                return true;
            }

            _ = ExecuteApprovedRealToolAsync(pending, conversationKey, executingSource, result.Reply, sendViaKey: false, msg: msg);
            return true;
        }

        if (result.Reply is { Length: > 0 } reply)
        {
            if (parsed.Kind == ApprovalCommandKind.Reject) _pendingTools.Remove(parsed.RequestId);
            _ = _hooks.SendApprovalReplyAsync(msg, reply);
        }

        return true;
    }

    /// <summary>
    /// 面板上的同意 / 拒绝（批次 I）。
    ///
    /// **校验一条都不放宽**：判定仍走 <see cref="ApprovalFlow.Handle" />（身份 / 有效期 / 一次性 / 策略版本），
    /// 执行前仍过一次闸门，执行体仍是那个**没有真实副作用**的固定假工具。
    ///
    /// 与群里那条路的**唯一差别**（写在这里，别当成顺手放宽）：
    ///   · **提交身份**：面板 = 管理员的控制台，因此以 owner 身份提交 ——
    ///     与“群主可以批”是**同一条规则**（见 ApprovalStore.IsAuthorizedApprover：按 id 点名 ∪ 按身份）。
    ///     于是开单时给了 owner/admin 角色的（群聊里开的单）面板批得动；
    ///     没给角色的（私聊里开的单，只有点名名单算数）面板同样批不动 —— **失败关闭，不放宽**。
    ///   · **会话 key 的来源**：会话绑定校验要求“来自发起审批的那个会话”，
    ///     而面板**不是会话**；这里用这张单子**自己的** key 提交，于是跨会话这件事根本不成立。
    ///
    /// 收尾与群里那条路一致：回执发回**原会话**（群里的人要看到“谁批了”），并记一个轨迹节点。
    /// </summary>
    /// <param name="requestId">审批单编号。</param>
    /// <param name="approve">true = 同意（批准并执行固定假工具）；false = 拒绝。</param>
    public (bool Ok, string ReasonCode) PanelDecide(string requestId, bool approve)
    {
        var now = EngineClock.Now;
        var request = _approvals.Get(requestId, now);
        if (request is null)
        {
            return (false, "unknown_request");
        }

        _sources.TryGetValue(requestId, out var originalSource);
        // #117：面板不是会话 —— 没有原始租约时**不能**用 Enter(null) 假装“没有来源约束”，
        // 直接失败关闭（与群里那条路“租约换代即不动作”是同一条规则）。
        if (originalSource is null) return (false, "source_unavailable");
        if (originalSource.IsCurrent is false) return (false, "source_retired");
        using var sourceScope = PlatformSourceExecution.Enter(originalSource);
        var conversationKey = request.ConversationKey;
        var result = ApprovalFlow.Handle(
            _approvals,
            new ApprovalCommand(approve ? ApprovalCommandKind.Approve : ApprovalCommandKind.Reject, requestId),
            requesterId: "panel:owner",
            requesterRole: "owner",
            conversationKey: conversationKey,
            now: now,
            currentPolicyVersion: Capabilities.Policy.PolicyVersion);

        _hooks.Log($"[审批] 面板 {requestId} → {result.ReasonCode}（{Channels.Describe(conversationKey)}）");

        if (_audit is not null && result.Handled)
        {
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                requestId = requestId,
                action = approve ? "approve" : "reject",
                reason = result.ReasonCode,
                shouldExecute = result.ShouldExecute
            });
            _audit.Append(new AuditEvent("approval_decision", "panel:owner", conversationKey, detail, Capabilities.Policy.PolicyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (result.ShouldExecute && result.Ticket is { } ticket)
        {
            // 执行前**再过一次闸门**（与群里那条路同一句）：闸门才是“能不能执行”的唯一判据。
            var toolId = ticket.ToolId;
            var liveCaps = ResolveLiveCapabilities(conversationKey, originalSource.Context);
            var gate = liveCaps.Check(toolId, conversationKey, ticket: ticket);
            if (gate.Allow && !string.Equals(toolId, ApprovalFlow.FixedToolId, StringComparison.Ordinal)
                && _pluginAllowsTool is not null && !_pluginAllowsTool(toolId))
            {
                gate = new ToolDecision(false, "plugin_disabled", "插件或工具已被禁用");
            }
            _traces.Node(conversationKey, TurnNodeKind.Gate, gate.Allow ? "allowed" : "denied",
                toolId: toolId, reasonCode: gate.ReasonCode);
            if (!gate.Allow)
            {
                _pendingTools.Remove(requestId);
                _hooks.Log($"[审批] 面板批准后闸门拒绝执行（{gate.ReasonCode}）→ 不执行：{Channels.Describe(conversationKey)}");
                return (false, gate.ReasonCode);
            }

            // #117：决策与执行之间平台源可能换代 —— 执行前再核一次，不靠决策时的快照。
            if (originalSource.IsCurrent is false)
            {
                _pendingTools.Remove(requestId);
                _hooks.Log($"[审批] 面板执行前平台源已换代（租约失效）→ 不执行：{Channels.Describe(conversationKey)}");
                return (false, "source_retired");
            }

            if (string.Equals(toolId, ApprovalFlow.FixedToolId, StringComparison.Ordinal))
            {
                RunFixedDemoToolFor(conversationKey, requestId, result.Reply);
                return (true, result.ReasonCode);
            }

            if (!_pendingTools.TryTake(requestId, out var pending)
                || !string.Equals(pending.ToolId, toolId, StringComparison.Ordinal)
                || !string.Equals(pending.ConversationKey, conversationKey, StringComparison.Ordinal))
            {
                _hooks.Log($"[审批] 面板真实工具参数快照缺失（{requestId}）→ 拒绝执行");
                return (false, "invocation_missing");
            }

            _ = ExecuteApprovedRealToolAsync(pending, conversationKey, originalSource, result.Reply, sendViaKey: true, msg: null);
            return (true, result.ReasonCode);
        }

        if (result.Handled && result.Reply is { Length: > 0 } reply)
        {
            if (!approve) _pendingTools.Remove(requestId);
            _ = SendToKeyAsync(conversationKey, reply);
        }

        return (result.Ok, result.ReasonCode);
    }

    /// <summary>面板那条路把回执发回原会话（没有入站消息可用，所以按 key 发）。</summary>
    private Task SendToKeyAsync(string conversationKey, string text)
        => _hooks.SendToKeyAsync is { } send ? send(conversationKey, text) : Task.CompletedTask;

    /// <summary>
    /// 「执行」固定假工具（面板那条路：没有入站消息，回执按会话 key 发）。
    /// 它同样**什么都不做**：只记一行日志 + 回一句说明。
    /// </summary>
    private void RunFixedDemoToolFor(string conversationKey, string requestId, string? reply)
    {
        _hooks.Log($"[审批] 面板执行固定假工具 {ApprovalFlow.FixedToolId}"
                   + $"（编号 {requestId}，**无真实副作用**）：{Channels.Describe(conversationKey)}");
        _traces.Node(conversationKey, TurnNodeKind.ToolExec, "ok",
            toolId: ApprovalFlow.FixedToolId, reasonCode: "panel_approved");

        if (reply is { Length: > 0 })
        {
            _ = SendToKeyAsync(conversationKey, reply);
        }
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
            now: EngineClock.Now,
            newRequestId: () => ApprovalFlow.NewRequestId(
                upper => System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper)),
            policyVersion: policyVersion);

        if (!created.Created)
        {
            // 空问题 / 同一会话已经挂着待答的问题 → 什么都不做（照旧安全静默）
            _hooks.Log($"[提问] 没有开单（{created.ReasonCode}）：{conversation.Name}");
            return false;
        }

        if (created.Request is { } question && PlatformSourceExecution.Current is { } questionSource)
            _sources[question.RequestId] = questionSource;

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
            conversation.SourceKey, ApprovalFlow.QuestionToolId, EngineClock.Now);
        if (pending is null)
        {
            return;
        }

        // 记下"谁答的"（身份摘要，日志里按脱敏开关显示）：V3 §8.1 要求待答问题**有身份**。
        // 产品口径仍是"谁答都算答完"（群聊里不 @ 也应一声是常态），但台账与运行记录里要能看出是谁。
        var answeredBy = "user:" + msg.UserId;
        var outcome = _approvals.MarkAnswered(
            pending.RequestId, conversation.SourceKey, EngineClock.Now, answeredBy);
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
        var approvers = Approvers();
        var created = ApprovalFlow.CreateForModelTool(
            _approvals,
            requestedToolId: toolId,
            conversationKey: conversation.SourceKey,
            requesterFingerprint: triggerMessageId is { } id ? "msg:" + id : "msg:unknown",
            configuredApprovers: approvers,
            allowGroupAdmins: !Channels.IsOfficial(Channels.ChannelOf(conversation.SourceKey)),
            isGroup: isGroup,
            now: EngineClock.Now,
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

        if (created.Request is { } approval && PlatformSourceExecution.Current is { } approvalSource)
            _sources[approval.RequestId] = approvalSource;

        _hooks.Log($"[审批] 新建待批单 {created.Request?.RequestId}（{ApprovalFlow.FixedToolId}"
                   + $"，v{policyVersion}，"
                   + $"{ApprovalFlow.TtlSeconds}s 有效）：{conversation.Name}");

        if (created.Announcement is { Length: > 0 } announcement)
        {
            using (ApprovalOutboundScope.EnterSystemAnnouncement())
            {
                await _hooks.SendPlainAsync(conversation, announcement);
            }
        }

        return true;
    }

    /// <summary>为真实的非只读工具（如非只读远程 MCP / 需批准插件工具）冻结参数并开一张人机审批单。</summary>
    public async Task<bool> OpenApprovalForRealToolAsync(
        BotConversation conversation,
        string? toolId,
        IReadOnlyDictionary<string, JsonNode?>? arguments,
        long? triggerMessageId,
        ChatCapabilitySet caps)
    {
        if (string.IsNullOrWhiteSpace(toolId) || Services.Memory.MemoryUseScope.HasMemory)
            return false;

        var cleanId = toolId.Trim();
        var binding = caps.Catalog.FindBinding(cleanId);
        if (binding is null || !binding.IsReady || binding.Definition.Spec.ReadOnly
            || ToolDescriptor.AlwaysDenied(binding.Definition.Spec.Category))
        {
            _hooks.Log($"[审批] 拒绝开单（工具不可用或不可审批：{cleanId}）：{conversation.Name}");
            return false;
        }

        var spec = binding.Definition.Spec;
        if (!caps.Policy.AllowedTools.Contains(cleanId)
            || !caps.Policy.RequiresApproval(spec)
            || !caps.Policy.ApprovalCouldGrant(spec))
        {
            _hooks.Log($"[审批] 策略禁止为工具开单（{cleanId}）：{conversation.Name}");
            return false;
        }

        var frozen = new JsonObject();
        if (arguments is not null)
        {
            foreach (var pair in arguments)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                frozen[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var safeSummary = PendingToolInvocationStore.BuildSanitizedSummary(cleanId, frozen);
        var (isGroup, _) = conversation.Target;
        var approvers = Approvers();
        approvers.Add("panel:owner");

        var requestId = ApprovalFlow.NewRequestId(
            upper => System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper));
        var now = EngineClock.Now;
        var expiresAt = now.AddSeconds(ApprovalFlow.TtlSeconds);
        _pendingTools.Put(new PendingToolInvocation(
            requestId, cleanId, conversation.SourceKey, frozen, safeSummary, expiresAt), now);
        if (PlatformSourceExecution.Current is { } approvalSource)
            _sources[requestId] = approvalSource;

        var created = ApprovalFlow.CreateForTool(
            _approvals,
            toolId: cleanId,
            safeSummary: safeSummary,
            conversationKey: conversation.SourceKey,
            requesterFingerprint: triggerMessageId is { } id ? "msg:" + id : "msg:unknown",
            configuredApprovers: approvers,
            allowGroupAdmins: !Channels.IsOfficial(Channels.ChannelOf(conversation.SourceKey)),
            isGroup: isGroup,
            now: now,
            newRequestId: () => requestId,
            policyVersion: caps.Policy.PolicyVersion);

        if (!created.Created || created.Request is not { } approval)
        {
            _pendingTools.Remove(requestId);
            _sources.TryRemove(requestId, out _);
            _hooks.Log($"[审批] 真实工具没有开单（{created.ReasonCode}，{cleanId}）：{conversation.Name}");
            return false;
        }

        _hooks.Log($"[审批] 新建真实工具待批单 {approval.RequestId}（{approval.ToolId}，v{caps.Policy.PolicyVersion}，{ApprovalFlow.TtlSeconds}s 有效）：{conversation.Name}");

        if (created.Announcement is { Length: > 0 } announcement)
        {
            using (ApprovalOutboundScope.EnterSystemAnnouncement())
            {
                await _hooks.SendPlainAsync(conversation, announcement);
            }
        }

        return true;
    }

    private async Task ExecuteApprovedRealToolAsync(
        PendingToolInvocation pending,
        string conversationKey,
        PlatformSourceLease? executingSource,
        string? approvalReceipt,
        bool sendViaKey,
        QqChatMessage? msg)
    {
        try
        {
            using var sourceScope = PlatformSourceExecution.Enter(executingSource);
            if (approvalReceipt is { Length: > 0 })
            {
                if (sendViaKey) await SendToKeyAsync(conversationKey, approvalReceipt).ConfigureAwait(false);
                else if (msg is not null) await _hooks.SendApprovalReplyAsync(msg, approvalReceipt).ConfigureAwait(false);
            }

            if (executingSource is { IsCurrent: false })
            {
                _hooks.Log($"[审批] 真实工具执行前平台源失效：{pending.ToolId}");
                return;
            }

            var liveConv = _resolveConversation?.Invoke(conversationKey, executingSource?.Context);
            var liveCaps = liveConv is not null ? CapabilitiesFor(_settings, liveConv) : Capabilities;
            var binding = liveCaps.Catalog.FindBinding(pending.ToolId);
            var convPolicyAllows = _settings.EnableApprovals
                && liveConv is not { AllowsToolRequest: false }
                && liveCaps.Policy.AllowedTools.Contains(pending.ToolId)
                && binding is not null
                && liveCaps.Policy.ApprovalCouldGrant(binding.Definition.Spec)
                && (_pluginAllowsTool is null || _pluginAllowsTool(pending.ToolId));
            if (binding is null || !binding.IsReady || binding.Definition.Spec.ReadOnly
                || ToolDescriptor.AlwaysDenied(binding.Definition.Spec.Category)
                || !convPolicyAllows)
            {
                _traces.Node(conversationKey, TurnNodeKind.ToolExec, "denied", toolId: pending.ToolId, reasonCode: "tool_unavailable");
                var deniedMsg = $"⚠️ 已批准的工具 {pending.ToolId} 当前不可用，未执行。";
                if (sendViaKey) await SendToKeyAsync(conversationKey, deniedMsg).ConfigureAwait(false);
                else if (msg is not null) await _hooks.SendApprovalReplyAsync(msg, deniedMsg).ConfigureAwait(false);
                return;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(executingSource?.Lifetime ?? default);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var outcome = await binding.ExecuteAsync(conversationKey, new ToolCall(pending.ToolId, pending.FrozenArguments), cts.Token).ConfigureAwait(false);
            _traces.Node(conversationKey, TurnNodeKind.ToolExec, outcome.Ok ? "ok" : "failed",
                toolId: pending.ToolId, reasonCode: outcome.ReasonCode);
            _hooks.Log($"[审批] 真实工具已执行 {pending.ToolId}（{pending.RequestId}） ok={outcome.Ok} reason={outcome.ReasonCode}");

            var snippet = outcome.ModelText ?? string.Empty;
            if (snippet.Length > 600) snippet = snippet[..600] + "…";
            var masked = snippet.Length > 0 ? _hooks.Mask(snippet, conversationKey) : "完成";
            var summaryText = outcome.Ok
                ? $"✅ 工具 {pending.ToolId} 已执行：\n{masked}"
                : $"❌ 工具 {pending.ToolId} 执行失败（{outcome.ReasonCode}）：{masked}";
            if (sendViaKey) await SendToKeyAsync(conversationKey, summaryText).ConfigureAwait(false);
            else if (msg is not null) await _hooks.SendApprovalReplyAsync(msg, summaryText).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _traces.Node(conversationKey, TurnNodeKind.ToolExec, "error", toolId: pending.ToolId, reasonCode: ex.GetType().Name);
            _hooks.Log($"[审批] 真实工具执行异常（{pending.ToolId}）：{ex.GetType().Name}");
            try
            {
                var errMsg = $"❌ 工具 {pending.ToolId} 执行异常（{ex.GetType().Name}）。";
                if (sendViaKey) await SendToKeyAsync(conversationKey, errMsg).ConfigureAwait(false);
                else if (msg is not null) await _hooks.SendApprovalReplyAsync(msg, errMsg).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private ChatCapabilitySet ResolveLiveCapabilities(string conversationKey, PlatformContext? context)
    {
        if (_resolveConversation is null) return Capabilities;
        var liveConv = _resolveConversation(conversationKey, context);
        return liveConv is not null ? CapabilitiesFor(_settings, liveConv) : Capabilities;
    }

    /// <summary>
    /// 审批台账周期清理（V3 §9.4：台账要有界）。跟着静默兜底的节奏走，顺手加个 10 分钟节流 ——
    /// 它只删已结束（过期/拒绝/取消/已答）的单子，不会动还在等待决定的那张。
    /// </summary>
    public void PruneIfDue(DateTimeOffset now)
    {
        _onPeriodicSweep?.Invoke(now);
        if (now - _lastPrune <= TimeSpan.FromMinutes(10))
        {
            return;
        }

        _lastPrune = now;
        var pruned = _approvals.Prune(now);
        _pendingTools.Prune(now);
        foreach (var id in _sources.Keys)
            if (_approvals.Get(id, now) is null) _sources.TryRemove(id, out _);
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
/// <param name="SendToKeyAsync">
/// 按**会话 key** 发一条纯文本（批次 I 的面板审批用）：面板没有“入站消息”可回，
/// 而回执仍然应该回到**发起审批的那个会话**里（群里的人要看到决定）。
/// </param>
public readonly record struct ApprovalHooks(
    Action<string> Log,
    Func<string, string?, string> Mask,
    Func<BotConversation, string, Task> SendPlainAsync,
    Func<QqChatMessage, string, Task> SendApprovalReplyAsync,
    Func<string, string, Task>? SendToKeyAsync = null);
