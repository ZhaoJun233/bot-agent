using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Rendering;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Scheduler;
using BotAgent.Services.Agent;
using BotAgent.Services.Digest;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Scheduler;

/// <summary>
/// 定时任务全通道路由投递适配器：
/// 1) 个人工作台作用域（<c>workspace:owner</c>）写入专属消息流并广播无正文增量事件；
/// 2) 聊天通道严格按顺序核查：QQ 官方被动边界 → 平台策略（启用+聊天开关）→ 白名单 → 来源当前租约 → 日配额 → 发送与回执 audit。
/// </summary>
public sealed class ScheduledDeliveryAdapter : IScheduledDeliveryPort
{
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly WhitelistGate _whitelist;
    private readonly PluginManager _plugins;
    private readonly ITenantQuotaLedger? _quotas;
    private readonly IWorkspaceConversationStore? _workspaceStore;
    private readonly Action<string>? _onWorkspaceAppended;
    private readonly Func<string, string, CancellationToken, Task<(bool Ok, string Code)>>? _dispatchTask;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string> _log;
    private readonly PluginGate? _pluginGate;

    public ScheduledDeliveryAdapter(
        SettingsBox box,
        IQqChatSource source,
        IPlatformPolicyResolver platformPolicies,
        WhitelistGate whitelist,
        PluginManager plugins,
        ITenantQuotaLedger? quotas = null,
        IWorkspaceConversationStore? workspaceStore = null,
        Action<string>? onWorkspaceAppended = null,
        Func<string, string, CancellationToken, Task<(bool Ok, string Code)>>? dispatchTask = null,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? log = null,
        PluginGate? pluginGate = null)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _platformPolicies = platformPolicies ?? throw new ArgumentNullException(nameof(platformPolicies));
        _whitelist = whitelist ?? throw new ArgumentNullException(nameof(whitelist));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _quotas = quotas;
        _workspaceStore = workspaceStore;
        _onWorkspaceAppended = onWorkspaceAppended;
        _dispatchTask = dispatchTask;
        _utcNow = utcNow ?? (() => Clock.UtcNow);
        _log = log ?? (_ => { });
        _pluginGate = pluginGate;
    }

    public async Task<ScheduledDeliveryResult> DeliverAsync(
        ScheduledJobRecord job,
        long scheduledUnix,
        string? missedNotice,
        CancellationToken ct)
    {
        if (!_box.Current.AiModeEnabled)
            return new(false, ScheduledDeliveryStatus.Skipped, "ai_mode_disabled");
        if (_pluginGate is not null && !_pluginGate.Allows(PluginGate.SchedulerCapability))
            return new(false, ScheduledDeliveryStatus.Skipped, "scheduler_disabled");
        if (ScheduledJobValidator.IsQqOfficialScope(job.ScopeKey))
            return new(false, ScheduledDeliveryStatus.Skipped, "passive_trigger_required");

        if (!ScheduledJobValidator.ValidatePayload(job.PayloadJson, out var rawText, out var payloadErr))
            return new(false, ScheduledDeliveryStatus.Failed, payloadErr);

        var quotaRejected = CheckQuota(job.ScopeKey);
        if (quotaRejected is not null)
            return quotaRejected;

        var isWorkspace = string.Equals(job.ScopeKey, SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal);

        // 离线超过 15 分钟的跳过告警：绝不补跑 CronTask / CronDigest 或过时提醒正文，只发轻量恢复提示。
        if (!string.IsNullOrWhiteSpace(missedNotice))
        {
            var noticeBody = $"[定时调度恢复提示 | {job.JobId}] {missedNotice}";
            if (isWorkspace)
                return DeliverToWorkspace(job, scheduledUnix, noticeBody);
            return await DeliverToChatScopeAsync(job.ScopeKey, noticeBody, ct).ConfigureAwait(false);
        }

        EffectiveConversationSettings? chatEffective = null;
        IPlatformSourceRouter? router = null;
        PlatformSourceLease? lease = null;
        if (!isWorkspace)
        {
            var admission = CheckChatScopeAdmission(job.ScopeKey, out router, out lease, out chatEffective);
            if (admission is not null) return admission;
        }

        if (job.Kind == ScheduledJobKind.CronTask)
        {
            if (!_box.Current.EnableAgentBridge)
                return new(false, ScheduledDeliveryStatus.Skipped, "agent_bridge_disabled");
            if (chatEffective is { AllowsReadOnlyTools: false })
                return new(false, ScheduledDeliveryStatus.Skipped, "conversation_profile_denied");
            if (_dispatchTask is null)
                return new(false, ScheduledDeliveryStatus.Failed, "task_runtime_unavailable");
            var (ok, code) = await _dispatchTask(job.ScopeKey, rawText, ct).ConfigureAwait(false);
            return new(ok, ok ? ScheduledDeliveryStatus.Succeeded : ScheduledDeliveryStatus.Failed, code);
        }

        if (job.Kind == ScheduledJobKind.CronDigest)
        {
            if (_pluginGate is not null && !_pluginGate.Allows(PluginGate.DigestCapability))
                return new(false, ScheduledDeliveryStatus.Skipped, "digest_disabled");
            if (chatEffective is not null && !chatEffective.Feature("digest").Enabled)
                return new(false, ScheduledDeliveryStatus.Skipped, "digest_disabled");
        }

        var body = await ResolveJobBodyAsync(job, rawText, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
            return new(false, ScheduledDeliveryStatus.Skipped, "empty_payload");

        if (isWorkspace)
            return DeliverToWorkspace(job, scheduledUnix, body);

        return await SendAdmittedChatBodyAsync(job.ScopeKey, body, router, lease, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveJobBodyAsync(ScheduledJobRecord job, string rawText, CancellationToken ct)
    {
        if (job.Kind == ScheduledJobKind.OnceReminder)
            return $"[定时提醒] {rawText}";
        if (job.Kind == ScheduledJobKind.CronText)
            return $"[周期提醒] {rawText}";
        if (job.Kind == ScheduledJobKind.CronDigest)
        {
            if (_plugins.TryGetContribution<IDigestContribution>("preset.feature.digest", out var digest)
                && digest is not null && digest.IsReady)
            {
                var result = await digest.SummarizeAsync(job.ScopeKey, 30, 24, string.IsNullOrWhiteSpace(rawText) ? null : rawText, ct)
                    .ConfigureAwait(false);
                if (result.Ok && !string.IsNullOrWhiteSpace(result.Summary))
                    return $"[定时纪要]\n{result.Summary}";
            }
            return string.IsNullOrWhiteSpace(rawText) ? string.Empty : $"[定时纪要] {rawText}";
        }
        return rawText;
    }

    private ScheduledDeliveryResult DeliverToWorkspace(ScheduledJobRecord job, long scheduledUnix, string body)
    {
        if (_workspaceStore is null)
            return new(false, ScheduledDeliveryStatus.Failed, "workspace_unavailable");

        var nowUnix = _utcNow().ToUnixTimeSeconds();
        var key = $"sched:{job.JobId}:{scheduledUnix}";
        _workspaceStore.Append("assistant", body, "scheduler", nowUnix, idempotencyKey: key);
        _onWorkspaceAppended?.Invoke(job.JobId);
        return new(true, ScheduledDeliveryStatus.Succeeded, "workspace_delivered");
    }

    private ScheduledDeliveryResult? CheckQuota(string scopeKey)
    {
        if (_quotas is not null && _quotas.IsEnergySaving(scopeKey))
            return new(false, ScheduledDeliveryStatus.Skipped, "quota_exceeded");
        if (_plugins.TryGetContribution<ITenantQuotaManagement>("preset.feature.token-quota", out var quotaMgmt)
            && quotaMgmt is not null && quotaMgmt.GetQuota(scopeKey).EnergySaving)
            return new(false, ScheduledDeliveryStatus.Skipped, "quota_exceeded");
        return null;
    }

    private ScheduledDeliveryResult? CheckChatScopeAdmission(
        string scopeKey,
        out IPlatformSourceRouter? router,
        out PlatformSourceLease? lease,
        out EffectiveConversationSettings? effective)
    {
        effective = null;
        router = _source as IPlatformSourceRouter;
        lease = router?.CaptureForConversation(scopeKey);
        var quotaRejected = CheckQuota(scopeKey);
        if (quotaRejected is not null)
            return quotaRejected;
        if (router is not null && (lease is null || !lease.IsCurrent))
            return new(false, ScheduledDeliveryStatus.Skipped, "platform_source_unavailable");

        var platformPolicy = lease is not null
            ? _platformPolicies.Resolve(lease.Context)
            : _platformPolicies.ResolveForChannel(Channels.ChannelOf(scopeKey));
        if (!platformPolicy.Registered || !platformPolicy.Connected)
            return new(false, ScheduledDeliveryStatus.Skipped, "platform_disconnected");
        effective = ConversationProfileResolver.Resolve(_box.Current, platformPolicy, scopeKey);
        var textDecision = effective.Feature("text");
        if (!textDecision.Enabled)
            return new(false, ScheduledDeliveryStatus.Skipped, textDecision.ReasonCode);
        var schedDecision = effective.Feature("scheduler");
        if (!schedDecision.Enabled)
            return new(false, ScheduledDeliveryStatus.Skipped, schedDecision.ReasonCode);

        if (!TryResolveTarget(scopeKey, router, out var isGroup, out var targetId))
            return new(false, ScheduledDeliveryStatus.Failed, "invalid_target");

        bool allowed;
        if (lease is not null && (_box.Current.RuntimePlatformPolicies.HasValue || lease.Context.PlatformId == PlatformId.QqPrivate))
        {
            using var scope = PlatformSourceExecution.Enter(lease);
            allowed = _whitelist.AllowsSource(lease.Context, isGroup, targetId);
        }
        else if (lease?.Source is BotAgent.Platforms.IFeishuTargetMap feishuMap)
        {
            allowed = feishuMap.AllowsMappedTarget(isGroup, targetId);
        }
        else
        {
            allowed = !Channels.IsFeishu(Channels.ChannelOf(scopeKey)) && _whitelist.AllowsKey(scopeKey);
        }
        return allowed ? null : new(false, ScheduledDeliveryStatus.Skipped, "whitelist_denied");
    }

    private async Task<ScheduledDeliveryResult> DeliverToChatScopeAsync(string scopeKey, string body, CancellationToken ct)
    {
        var rejected = CheckChatScopeAdmission(scopeKey, out var router, out var lease, out _);
        if (rejected is not null)
            return rejected;
        return await SendAdmittedChatBodyAsync(scopeKey, body, router, lease, ct).ConfigureAwait(false);
    }

    private async Task<ScheduledDeliveryResult> SendAdmittedChatBodyAsync(
        string scopeKey, string body, IPlatformSourceRouter? router, PlatformSourceLease? lease, CancellationToken ct)
    {

        var audit = ReplyAuditRules.Judge(body, allowLocalPaths: false);
        if (audit != ReplyAuditVerdict.Allow)
            return new(false, ScheduledDeliveryStatus.Skipped, ReplyAuditRules.Code(audit));

        if (!TryResolveTarget(scopeKey, router, out var isGroup, out var targetId))
            return new(false, ScheduledDeliveryStatus.Failed, "invalid_target");

        using var leaseScope = PlatformSourceExecution.Enter(lease);
        using var commandScope = BotAgent.Services.Memory.MemoryUseScope.EnterCommandResponse();
        await ReplyCommitBoundary.BeforeEffectAsync().ConfigureAwait(false);
        var sent = await _source.SendTextAsync(isGroup, targetId, body, ct).ConfigureAwait(false);
        if (sent.Ok)
        {
            _log($"[Scheduler] 定时消息已投递（scope={AgentMask.ChatLabel(scopeKey)}，len={body.Length}）");
            return new(true, ScheduledDeliveryStatus.Succeeded, "delivered");
        }
        return new(false, ScheduledDeliveryStatus.Failed, sent.Delivery?.ReasonCode ?? "send_failed");
    }

    private static bool TryResolveTarget(string scopeKey, IPlatformSourceRouter? router, out bool isGroup, out long targetId)
    {
        if (router is not null)
        {
            (isGroup, targetId) = router.CompatibilityTarget(scopeKey);
            if (targetId > 0) return true;
        }
        (isGroup, targetId) = Channels.Parse(scopeKey);
        if (targetId > 0) return true;

        if (ConversationIdCodec.TryParse(scopeKey, out var conv)
            && long.TryParse(conv.NativeTargetId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            isGroup = conv.Kind == ConversationKind.GroupChat;
            targetId = parsed;
            return true;
        }
        return false;
    }
}
