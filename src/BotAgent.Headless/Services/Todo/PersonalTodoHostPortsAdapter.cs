using System.Collections.Concurrent;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Identity;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Scheduler;
using BotAgent.Services.OneBot;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Scheduler;

namespace BotAgent.Services.Todo;

/// <summary>
/// <c>preset.feature.personal-todo</c> 的宿主端口适配器：
/// 1) 严格限定仅可靠私聊或本人工作台（<c>workspace:owner</c>）可读/写，群聊与不可靠身份一律返回 <c>private_conversation_only</c>；
/// 2) 带截止时间的待办自动联动 <see cref="IScheduledJobStore"/> 创建一次性提醒；完成/删除时级联取消提醒；
/// 3) 仅当启用 P5.3 跨端绑定且显式勾选共享待办时映射至共享 <c>canonical:&lt;id&gt;</c> 作用域。
/// </summary>
public sealed class PersonalTodoHostPortsAdapter : IPersonalTodoHostPorts
{
    private const int MaxTrackedObserved = 1024;

    private readonly IPersonalTodoStore _store;
    private readonly IScheduledJobStore? _schedulerStore;
    private readonly IIdentityLinkResolver? _identityLinks;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action? _onScheduleChanged;
    private readonly SettingsBox? _box;
    private readonly PluginGate? _pluginGate;
    private readonly ConcurrentDictionary<string, (bool IsPrivate, bool IsReliable, IdentityEndpointKey? Endpoint)> _observed = new(StringComparer.Ordinal);

    public PersonalTodoHostPortsAdapter(
        IPersonalTodoStore store,
        IScheduledJobStore? schedulerStore = null,
        IIdentityLinkResolver? identityLinks = null,
        Func<DateTimeOffset>? utcNow = null,
        Action? onScheduleChanged = null,
        SettingsBox? box = null,
        PluginGate? pluginGate = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _schedulerStore = schedulerStore;
        _identityLinks = identityLinks;
        _utcNow = utcNow ?? (() => Clock.UtcNow);
        _onScheduleChanged = onScheduleChanged;
        _box = box;
        _pluginGate = pluginGate;
    }

    public void ObserveSourceContext(QqChatMessage msg, string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        IdentityEndpointKey? endpoint = null;
        var isPrivate = !msg.IsGroup;
        var isReliable = msg.Envelope?.IsReliableIdentity ?? (msg.IsReliableIdentity == true);

        if (msg.Envelope is { IsReliableIdentity: true } env)
        {
            isReliable = true;
            isPrivate = env.Message.Ref.Conversation.Kind == ConversationKind.PrivateChat;
            var d = env.Origin.Driver;
            endpoint = new IdentityEndpointKey(d.PlatformId, d.AccountScope, d.InstanceId ?? string.Empty, env.Message.Sender.NativeUserId);
        }
        else if (isPrivate && PlatformSourceExecution.Current is { IsCurrent: true } current)
        {
            var nativeId = msg.UserId > 0 ? msg.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
            if (!string.IsNullOrEmpty(nativeId))
            {
                endpoint = new IdentityEndpointKey(current.Context.PlatformId, current.Context.AccountScope, current.Context.InstanceId ?? string.Empty, nativeId);
            }
        }
        else if (isPrivate && msg.UserId > 0)
        {
            var ch = Channels.ChannelOf(sourceKey);
            var pid = Channels.IsOfficial(ch) ? PlatformId.QqOfficial
                : Channels.IsFeishu(ch) ? PlatformId.Feishu
                : Channels.IsLocal(ch) ? PlatformId.Local
                : PlatformId.QqPrivate;
            var scope = pid == PlatformId.Feishu ? AccountScope.Default : AccountScope.Legacy;
            endpoint = new IdentityEndpointKey(pid, scope, ResolveDeclaredInstanceId(pid, scope), msg.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (_observed.Count >= MaxTrackedObserved && !_observed.ContainsKey(sourceKey))
        {
            foreach (var stale in _observed.Keys.Take(64).ToArray())
                _observed.TryRemove(stale, out _);
        }
        _observed[sourceKey] = (isPrivate, isReliable, endpoint);
    }

    public void ClearConversation(string sourceKey)
    {
        if (!string.IsNullOrWhiteSpace(sourceKey))
            _observed.TryRemove(sourceKey, out _);
    }

    public PersonalTodoMutationResult Add(
        string sourceKey,
        string title,
        string? notes,
        int priority,
        string? dueText,
        string? sourceHint,
        string? timezoneIana)
    {
        if (!TryResolveAuthorizedOwnerScope(sourceKey, out var ownerScope, out var denial))
            return new(false, false, denial);

        var tzId = string.IsNullOrWhiteSpace(timezoneIana) ? SchedulerLimits.DefaultTimeZone : timezoneIana.Trim();
        if (!CronSchedule.TryResolveTimeZone(tzId, out var tz))
            return new(false, false, "invalid_timezone");

        var now = _utcNow();
        var nowUnix = now.ToUnixTimeSeconds();
        long? dueUnix = null;
        if (!string.IsNullOrWhiteSpace(dueText))
        {
            if (!SchedulerHostPortsAdapter.TryParseWhenTime(dueText.Trim(), now, tz, out var fireUtc))
                return new(false, false, "scheduler_time_invalid");
            var candidateUnix = fireUtc.ToUnixTimeSeconds();
            if (candidateUnix <= nowUnix)
                return new(false, false, "scheduler_fire_time_in_past");
            dueUnix = candidateUnix;
        }

        var todoId = "todo-" + Guid.NewGuid().ToString("N")[..12];
        string? linkedJobId = null;
        var schedulerAllowed = _pluginGate?.Allows(PluginGate.SchedulerCapability) ?? true;

        if (dueUnix is { } fireUnix && schedulerAllowed && _schedulerStore is not null && !ScheduledJobValidator.IsQqOfficialScope(sourceKey))
        {
            var safeTitle = MessageMarkers.EscapeExternalControlTags(title.Trim());
            var reminderJob = new ScheduledJobRecord(
                JobId: "job-todo-" + todoId[5..],
                ScopeKey: sourceKey,
                CreatorUserId: "personal-todo",
                Kind: ScheduledJobKind.OnceReminder,
                CronExpr: null,
                TimezoneIana: tzId,
                PayloadJson: JsonSerializer.Serialize(new { text = $"待办到期提醒：{safeTitle}" }),
                MissedPolicy: MissedFirePolicy.SkipAndNotify,
                Enabled: true,
                NextFireUnix: fireUnix,
                LastFiredUnix: null,
                FailureCount: 0,
                CreatedUnix: nowUnix,
                UpdatedUnix: nowUnix);
            var jobRes = _schedulerStore.UpsertJob(reminderJob);
            if (jobRes.Ok)
            {
                linkedJobId = reminderJob.JobId;
                _onScheduleChanged?.Invoke();
            }
        }

        var item = new PersonalTodoItem(
            TodoId: todoId,
            OwnerScopeKey: ownerScope,
            Title: title,
            Notes: notes,
            Priority: Math.Clamp(priority, 1, 3),
            Status: PersonalTodoStatus.Open,
            DueUnix: dueUnix,
            LinkedJobId: linkedJobId,
            SourceHint: sourceHint,
            CreatedUnix: nowUnix,
            UpdatedUnix: nowUnix);

        var res = _store.SaveItem(ownerScope, item);
        if (!res.Ok && !string.IsNullOrEmpty(linkedJobId))
        {
            _schedulerStore?.DeleteJob(linkedJobId);
        }
        return res;
    }

    public PersonalTodoItem[] List(string sourceKey, bool includeDone = false)
    {
        if (!TryResolveAuthorizedOwnerScope(sourceKey, out var ownerScope, out _))
            return [];

        var items = _store.ReadOwner(ownerScope).Items;
        return includeDone
            ? items.OrderBy(i => i.Priority).ThenBy(i => i.DueUnix ?? long.MaxValue).ToArray()
            : items.Where(i => i.Status == PersonalTodoStatus.Open)
                .OrderBy(i => i.Priority)
                .ThenBy(i => i.DueUnix ?? long.MaxValue)
                .ToArray();
    }

    public PersonalTodoMutationResult Complete(string sourceKey, string todoId)
    {
        if (!TryResolveAuthorizedOwnerScope(sourceKey, out var ownerScope, out var denial))
            return new(false, false, denial);

        var snap = _store.ReadOwner(ownerScope);
        var existing = snap.Items.FirstOrDefault(t => string.Equals(t.TodoId, todoId, StringComparison.Ordinal));
        if (existing is null)
            return new(false, false, "todo_not_found");

        var nowUnix = _utcNow().ToUnixTimeSeconds();
        var completed = existing with { Status = PersonalTodoStatus.Done, LinkedJobId = null, UpdatedUnix = nowUnix };
        var res = _store.SaveItem(ownerScope, completed, expectedRevision: snap.Revision);
        if (res.Ok && !string.IsNullOrWhiteSpace(existing.LinkedJobId))
        {
            _schedulerStore?.DeleteJob(existing.LinkedJobId);
            _onScheduleChanged?.Invoke();
        }
        return res;
    }

    public PersonalTodoMutationResult Delete(string sourceKey, string todoId)
    {
        if (!TryResolveAuthorizedOwnerScope(sourceKey, out var ownerScope, out var denial))
            return new(false, false, denial);

        var res = _store.DeleteItem(ownerScope, todoId);
        if (res.Ok && res.Item is { LinkedJobId: { Length: > 0 } jobId })
        {
            _schedulerStore?.DeleteJob(jobId);
            _onScheduleChanged?.Invoke();
        }
        return res;
    }

    public bool TryResolveAuthorizedOwnerScope(string? sourceKey, out string ownerScopeKey, out string errorCode)
    {
        ownerScopeKey = string.Empty;
        errorCode = "private_conversation_only";
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;

        var trimmed = sourceKey.Trim();
        if (string.Equals(trimmed, SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal))
        {
            var shared = _identityLinks?.ResolveWorkspaceSharedScope(IdentityResourceKind.Todos);
            ownerScopeKey = !string.IsNullOrWhiteSpace(shared) ? $"shared:{shared}" : SchedulerLimits.WorkspaceOwnerScopeKey;
            errorCode = string.Empty;
            return true;
        }

        if (_observed.TryGetValue(trimmed, out var info))
        {
            if (!info.IsPrivate || !info.IsReliable) return false;
            if (info.Endpoint is not null
                && _identityLinks?.ResolveSharedOwnerScope(info.Endpoint, IdentityResourceKind.Todos) is { Length: > 0 } shared)
            {
                ownerScopeKey = $"shared:{shared}";
                errorCode = string.Empty;
                return true;
            }
            ownerScopeKey = $"private-scope:{trimmed}";
            errorCode = string.Empty;
            return true;
        }

        if (ConversationIdCodec.TryParse(trimmed, out var conv))
        {
            if (conv.Kind != ConversationKind.PrivateChat
                || string.Equals(conv.PlatformId, PlatformId.QqOfficial, StringComparison.OrdinalIgnoreCase))
                return false;
            // 未经入站可靠身份认证的纯字符串兜底绝不映射到跨端共享作用域。
            ownerScopeKey = $"private-scope:{trimmed}";
            errorCode = string.Empty;
            return true;
        }

        if (trimmed.StartsWith("private:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("feishu:private:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("local:private:", StringComparison.OrdinalIgnoreCase))
        {
            ownerScopeKey = $"private-scope:{trimmed}";
            errorCode = string.Empty;
            return true;
        }

        return false;
    }

    private string ResolveDeclaredInstanceId(string platformId, string accountScope)
    {
        var declared = _box?.Current.DeclaredPlatformDrivers ?? default;
        if (declared.IsDefaultOrEmpty) return string.Empty;
        DriverKey? match = null;
        var count = 0;
        var norm = PlatformId.Normalize(platformId);
        foreach (var d in declared)
        {
            if (string.Equals(PlatformId.Normalize(d.PlatformId), norm, StringComparison.Ordinal)
                && string.Equals(d.AccountScope, accountScope, StringComparison.Ordinal))
            {
                match = d;
                if (++count > 1) return string.Empty;
            }
        }
        return count == 1 && match is { } found ? found.InstanceId : string.Empty;
    }
}
