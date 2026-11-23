using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Identity;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Scheduler;
using BotAgent.Engine.Scheduler;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Scheduler;
using BotAgent.Services.Todo;
using BotAgent.Services.Workspace;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    private IScheduledJobStore? _schedulerStore;
    private ScheduledJobRunner? _schedulerRunner;
    private PersonalTodoHostPortsAdapter? _todoPorts;
    private IPersonalTodoStore? _todoStore;
    private IIdentityLinkResolver? _identityLinks;
    private WorkspaceService? _workspace;

    internal void AttachPhase5(
        IScheduledJobStore? schedulerStore,
        ScheduledJobRunner? schedulerRunner,
        PersonalTodoHostPortsAdapter? todoPorts,
        IPersonalTodoStore? todoStore,
        IIdentityLinkResolver? identityLinks,
        WorkspaceService? workspace)
    {
        _schedulerStore = schedulerStore;
        _schedulerRunner = schedulerRunner;
        _todoPorts = todoPorts;
        _todoStore = todoStore;
        _identityLinks = identityLinks;
        _workspace = workspace;
    }

    internal ValueTask<ZbaSchedulerJobsResponse> ListScheduledJobsAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        if (_schedulerStore is null) throw new PluginHttpException(503, "scheduler_unavailable");
        var isAdmin = (caller.Access & ConfigurationAccess.SaveConfiguration) != 0;
        return ValueTask.FromResult(BuildSchedulerJobsResponse(_schedulerStore.ReadSnapshot(), _box.Current.AgentMaskSensitive, isAdmin));
    }

    internal ValueTask<ZbaSchedulerWriteResult> SaveScheduledJobAsync(
        ConfigurationCaller caller,
        ZbaSchedulerSaveRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_schedulerStore is null) throw new PluginHttpException(503, "scheduler_unavailable");
        if (request is null || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new PluginHttpException(400, "invalid_request");

        if (!string.IsNullOrWhiteSpace(request.JobId) && !ScheduledJobValidator.IsValidJobId(request.JobId.Trim()))
            throw new ZbaBackendException(400, "job_id_invalid", "定时任务 ID 无效。");

        var now = _now();
        var nowUnix = now.ToUnixTimeSeconds();
        var tz = string.IsNullOrWhiteSpace(request.TimezoneIana) ? SchedulerLimits.DefaultTimeZone : request.TimezoneIana.Trim();
        var missed = string.IsNullOrWhiteSpace(request.MissedPolicy) ? MissedFirePolicy.SkipAndNotify : request.MissedPolicy.Trim();
        var payloadJson = JsonSerializer.Serialize(new { text = (request.Text ?? string.Empty).Trim() });

        if (!ScheduledJobValidator.ValidateJobInput(
                request.ScopeKey,
                request.Kind,
                request.CronExpr,
                tz,
                payloadJson,
                missed,
                request.NextFireUnix,
                now,
                out _,
                out var resolvedNextFire,
                out var err))
        {
            throw new ZbaBackendException(400, err, "定时任务参数无效。");
        }

        var jobId = string.IsNullOrWhiteSpace(request.JobId)
            ? "job-" + Guid.NewGuid().ToString("N")[..12]
            : request.JobId.Trim();
        var scopeKeyTrimmed = request.ScopeKey.Trim();
        var kindTrimmed = request.Kind.Trim();
        var cronTrimmed = string.IsNullOrWhiteSpace(request.CronExpr) ? null : request.CronExpr.Trim();
        var requestDigest = ComputeDigest(
            request.JobId?.Trim() ?? string.Empty,
            scopeKeyTrimmed,
            kindTrimmed,
            cronTrimmed,
            tz,
            payloadJson,
            missed,
            request.Enabled,
            request.NextFireUnix);

        var record = new ScheduledJobRecord(
            JobId: jobId,
            ScopeKey: scopeKeyTrimmed,
            CreatorUserId: caller.SubjectId,
            Kind: kindTrimmed,
            CronExpr: cronTrimmed,
            TimezoneIana: tz,
            PayloadJson: payloadJson,
            MissedPolicy: missed,
            Enabled: request.Enabled,
            NextFireUnix: resolvedNextFire,
            LastFiredUnix: null,
            FailureCount: 0,
            CreatedUnix: nowUnix,
            UpdatedUnix: nowUnix);

        var res = _schedulerStore.UpsertJob(record, request.ExpectedRevision, request.IdempotencyKey, requestDigest);
        if (!res.Ok)
        {
            var status = res.ReasonCode is "scheduler_revision_conflict" or "idempotency_key_conflict" ? 409 : 400;
            throw new ZbaBackendException(status, res.ReasonCode, "保存定时任务失败。");
        }

        _schedulerRunner?.NotifyScheduleChanged();
        return ValueTask.FromResult(new ZbaSchedulerWriteResult(
            true, res.Replayed, res.ReasonCode,
            BuildSchedulerJobsResponse(_schedulerStore.ReadSnapshot(), _box.Current.AgentMaskSensitive, isAdmin: true)));
    }

    internal ValueTask<ZbaSchedulerWriteResult> DeleteScheduledJobAsync(
        ConfigurationCaller caller,
        ZbaSchedulerDeleteRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_schedulerStore is null) throw new PluginHttpException(503, "scheduler_unavailable");
        if (request is null || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.JobId))
            throw new PluginHttpException(400, "invalid_request");

        var res = _schedulerStore.DeleteJob(request.JobId.Trim(), request.ExpectedRevision, request.IdempotencyKey);
        if (!res.Ok)
        {
            var status = res.ReasonCode is "scheduler_revision_conflict" or "idempotency_key_conflict" ? 409
                : res.ReasonCode == "job_not_found" ? 404 : 400;
            throw new ZbaBackendException(status, res.ReasonCode, "删除定时任务失败。");
        }

        _schedulerRunner?.NotifyScheduleChanged();
        return ValueTask.FromResult(new ZbaSchedulerWriteResult(
            true, res.Replayed, res.ReasonCode,
            BuildSchedulerJobsResponse(_schedulerStore.ReadSnapshot(), _box.Current.AgentMaskSensitive, isAdmin: true)));
    }

    internal ValueTask<ZbaSchedulerRunsResponse> ListScheduledRunsAsync(
        ConfigurationCaller caller,
        ZbaSchedulerRunsRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        if (_schedulerStore is null) throw new PluginHttpException(503, "scheduler_unavailable");
        var runs = _schedulerStore.ListRuns(request?.JobId, request?.Limit ?? 50)
            .Select(r => new ZbaScheduledJobRunView(r.RunKey, r.JobId, r.ScheduledUnix, r.StartedUnix, r.FinishedUnix, r.DeliveryStatus, r.ReasonCode))
            .ToArray();
        return ValueTask.FromResult(new ZbaSchedulerRunsResponse(runs));
    }

    internal ValueTask<ZbaPersonalTodosResponse> ListPersonalTodosAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_todoStore is null || _todoPorts is null) throw new PluginHttpException(503, "personal_todo_unavailable");
        if (!_todoPorts.TryResolveAuthorizedOwnerScope(SchedulerLimits.WorkspaceOwnerScopeKey, out var ownerScope, out _))
            throw new PluginHttpException(403, "private_conversation_only");
        return ValueTask.FromResult(BuildTodosResponse(_todoStore.ReadOwner(ownerScope)));
    }

    internal ValueTask<ZbaPersonalTodoWriteResult> SavePersonalTodoAsync(
        ConfigurationCaller caller,
        ZbaPersonalTodoSaveRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_todoStore is null || _todoPorts is null) throw new PluginHttpException(503, "personal_todo_unavailable");
        if (request is null || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new PluginHttpException(400, "invalid_request");
        if (!_todoPorts.TryResolveAuthorizedOwnerScope(SchedulerLimits.WorkspaceOwnerScopeKey, out var ownerScope, out var err))
            throw new ZbaBackendException(403, err, "仅限本人工作台或可靠私聊访问个人待办。");

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > PersonalTodoLimits.MaxTitleChars || title.Any(char.IsControl))
            throw new ZbaBackendException(400, "todo_title_invalid", "待办标题无效。");
        if (!PersonalTodoStatus.IsValid(request.Status))
            throw new ZbaBackendException(400, "todo_status_invalid", "待办状态无效。");
        if (!string.IsNullOrWhiteSpace(request.TodoId) && !ScheduledJobValidator.IsValidJobId(request.TodoId.Trim()))
            throw new ZbaBackendException(400, "todo_id_invalid", "待办 ID 无效。");

        var nowUnix = _now().ToUnixTimeSeconds();
        var tz = string.IsNullOrWhiteSpace(request.TimezoneIana) ? SchedulerLimits.DefaultTimeZone : request.TimezoneIana.Trim();
        if (!CronSchedule.TryResolveTimeZone(tz, out _))
            throw new ZbaBackendException(400, "invalid_timezone", "时区无效。");
        if (request.Status == PersonalTodoStatus.Open && request.DueUnix is { } dCheck && dCheck > 0 && dCheck <= nowUnix)
            throw new ZbaBackendException(400, "scheduler_fire_time_in_past", "提醒时间必须晚于当前时间。");

        var currentSnap = _todoStore.ReadOwner(ownerScope);
        var todoId = string.IsNullOrWhiteSpace(request.TodoId)
            ? "todo-" + Guid.NewGuid().ToString("N")[..12]
            : request.TodoId.Trim();
        var existing = currentSnap.Items.FirstOrDefault(t => string.Equals(t.TodoId, todoId, StringComparison.Ordinal));
        var linkedJobId = existing?.LinkedJobId;

        var requestDigest = ComputeDigest(
            request.TodoId?.Trim() ?? string.Empty,
            title,
            request.Notes?.Trim(),
            Math.Clamp(request.Priority, 1, 3),
            request.Status,
            request.DueUnix,
            request.SourceHint?.Trim(),
            tz);

        var item = new PersonalTodoItem(
            TodoId: todoId,
            OwnerScopeKey: ownerScope,
            Title: title,
            Notes: request.Notes,
            Priority: request.Priority,
            Status: request.Status,
            DueUnix: request.DueUnix,
            LinkedJobId: linkedJobId,
            SourceHint: request.SourceHint ?? existing?.SourceHint,
            CreatedUnix: existing?.CreatedUnix ?? nowUnix,
            UpdatedUnix: nowUnix);

        var res = _todoStore.SaveItem(ownerScope, item, request.ExpectedRevision, request.IdempotencyKey, requestDigest);
        if (!res.Ok)
        {
            var status = res.ReasonCode is "todo_revision_conflict" or "idempotency_key_conflict" ? 409 : 400;
            throw new ZbaBackendException(status, res.ReasonCode, "保存个人待办失败。");
        }

        if (!res.Replayed && res.Item is { } saved)
        {
            var nextLinked = saved.LinkedJobId;
            if (saved.Status != PersonalTodoStatus.Open || saved.DueUnix is not > 0)
            {
                if (!string.IsNullOrWhiteSpace(nextLinked))
                {
                    _schedulerStore?.DeleteJob(nextLinked);
                    _schedulerRunner?.NotifyScheduleChanged();
                    _todoStore.SaveItem(ownerScope, saved with { LinkedJobId = null });
                }
            }
            else if (_schedulerStore is not null && IsSchedulerEnabledForTodo())
            {
                var targetJobId = !string.IsNullOrWhiteSpace(nextLinked) ? nextLinked : BuildTodoLinkedJobId(saved.TodoId);
                var safeReminderTitle = BotAgent.Domain.Conversation.MessageMarkers.EscapeExternalControlTags(saved.Title);
                var reminder = new ScheduledJobRecord(
                    JobId: targetJobId,
                    ScopeKey: SchedulerLimits.WorkspaceOwnerScopeKey,
                    CreatorUserId: caller.SubjectId,
                    Kind: ScheduledJobKind.OnceReminder,
                    CronExpr: null,
                    TimezoneIana: tz,
                    PayloadJson: JsonSerializer.Serialize(new { text = $"待办到期提醒：{safeReminderTitle}" }),
                    MissedPolicy: MissedFirePolicy.SkipAndNotify,
                    Enabled: true,
                    NextFireUnix: saved.DueUnix.Value,
                    LastFiredUnix: null,
                    FailureCount: 0,
                    CreatedUnix: nowUnix,
                    UpdatedUnix: nowUnix);
                if (_schedulerStore.UpsertJob(reminder).Ok)
                {
                    _schedulerRunner?.NotifyScheduleChanged();
                    if (!string.Equals(saved.LinkedJobId, targetJobId, StringComparison.Ordinal))
                        _todoStore.SaveItem(ownerScope, saved with { LinkedJobId = targetJobId });
                }
            }
        }

        return ValueTask.FromResult(new ZbaPersonalTodoWriteResult(
            true, res.Replayed, res.ReasonCode, BuildTodosResponse(_todoStore.ReadOwner(ownerScope))));
    }

    internal ValueTask<ZbaPersonalTodoWriteResult> DeletePersonalTodoAsync(
        ConfigurationCaller caller,
        ZbaPersonalTodoDeleteRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_todoStore is null || _todoPorts is null) throw new PluginHttpException(503, "personal_todo_unavailable");
        if (request is null || string.IsNullOrWhiteSpace(request.IdempotencyKey) || string.IsNullOrWhiteSpace(request.TodoId))
            throw new PluginHttpException(400, "invalid_request");
        if (!_todoPorts.TryResolveAuthorizedOwnerScope(SchedulerLimits.WorkspaceOwnerScopeKey, out var ownerScope, out var err))
            throw new ZbaBackendException(403, err, "仅限本人工作台或可靠私聊访问个人待办。");

        var res = _todoStore.DeleteItem(ownerScope, request.TodoId.Trim(), request.ExpectedRevision, request.IdempotencyKey);
        if (!res.Ok)
        {
            var status = res.ReasonCode is "todo_revision_conflict" or "idempotency_key_conflict" ? 409
                : res.ReasonCode == "todo_not_found" ? 404 : 400;
            throw new ZbaBackendException(status, res.ReasonCode, "删除个人待办失败。");
        }
        if (res.Item is { LinkedJobId: { Length: > 0 } jobId })
        {
            _schedulerStore?.DeleteJob(jobId);
            _schedulerRunner?.NotifyScheduleChanged();
        }
        return ValueTask.FromResult(new ZbaPersonalTodoWriteResult(
            true, res.Replayed, res.ReasonCode, BuildTodosResponse(_todoStore.ReadOwner(ownerScope))));
    }

    internal ValueTask<ZbaWorkspaceMessagesResponse> ReadWorkspaceMessagesAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_workspace is null) throw new PluginHttpException(503, "workspace_unavailable");
        return ValueTask.FromResult(_workspace.ReadMessages());
    }

    internal async ValueTask<ZbaWorkspaceSendResult> SendWorkspaceMessageAsync(
        ConfigurationCaller caller,
        ZbaWorkspaceSendRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_workspace is null) throw new PluginHttpException(503, "workspace_unavailable");
        var res = await _workspace.SendAsync(request, ct).ConfigureAwait(false);
        if (!res.Ok && res.ReasonCode is "attachment_too_large" or "attachment_invalid_base64" or "executable_rejected"
            or "zip_bomb_rejected" or "file_too_large" or "workspace_text_required" or "workspace_text_too_long" or "invalid_idempotency_key")
        {
            throw new ZbaBackendException(400, res.ReasonCode, "工作台发送参数无效。");
        }
        return res;
    }

    internal ValueTask<ZbaWorkspaceClearResult> ClearWorkspaceMessagesAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_workspace is null) throw new PluginHttpException(503, "workspace_unavailable");
        return ValueTask.FromResult(_workspace.ClearMessages());
    }

    internal ValueTask<ZbaWorkspaceIdentityResponse> ReadWorkspaceIdentityAsync(ConfigurationCaller caller, CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.ReadOverview);
        ct.ThrowIfCancellationRequested();
        if (_workspace is null) throw new PluginHttpException(503, "workspace_unavailable");
        return ValueTask.FromResult(_workspace.ReadIdentity());
    }

    internal ValueTask<ZbaWorkspaceIdentityResponse> ToggleWorkspaceIdentityAsync(
        ConfigurationCaller caller,
        ZbaWorkspaceIdentityToggleRequest request,
        CancellationToken ct)
    {
        using var scope = Authorize(caller, ConfigurationAccess.SaveConfiguration);
        ct.ThrowIfCancellationRequested();
        if (_workspace is null) throw new PluginHttpException(503, "workspace_unavailable");
        if (request is null) throw new PluginHttpException(400, "invalid_request");
        var (response, errorCode) = _workspace.ToggleIdentityChecked(request);
        if (!string.IsNullOrEmpty(errorCode))
            throw new ZbaBackendException(400, errorCode, "工作台身份绑定配置无效。");
        return ValueTask.FromResult(response);
    }

    private bool IsSchedulerEnabledForTodo()
        => _plugins is not BotAgent.Services.Plugins.PluginManager mgr
            || (mgr.TryGetContribution<ISchedulerContribution>("preset.feature.scheduler", out var c) && c is { IsReady: true });

    private static string BuildTodoLinkedJobId(string todoId)
    {
        if (todoId.StartsWith("todo-", StringComparison.Ordinal) && todoId.Length >= 17)
            return "job-todo-" + todoId[5..];
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(todoId)))[..12];
        return "job-todo-" + hash;
    }

    private static string ComputeDigest(params object?[] parts)
    {
        var raw = JsonSerializer.Serialize(parts);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
    }

    private static ZbaSchedulerJobsResponse BuildSchedulerJobsResponse(SchedulerSnapshotView snap, bool maskSensitive, bool isAdmin)
    {
        var views = snap.Jobs.Select(j =>
        {
            ScheduledJobValidator.ValidatePayload(j.PayloadJson, out var text, out _);
            var isWorkspace = string.Equals(j.ScopeKey, SchedulerLimits.WorkspaceOwnerScopeKey, StringComparison.Ordinal);
            var masked = !isWorkspace && (!isAdmin || maskSensitive) ? AgentMask.ChatLabel(j.ScopeKey) : j.ScopeKey;
            var redactForRead = !isAdmin;
            return new ZbaScheduledJobView(
                j.JobId,
                redactForRead ? masked : j.ScopeKey,
                masked,
                j.Kind,
                j.CronExpr,
                j.TimezoneIana,
                redactForRead ? "[受保护]" : text,
                j.MissedPolicy,
                j.Enabled,
                j.NextFireUnix,
                j.LastFiredUnix,
                j.FailureCount);
        }).ToArray();
        return new ZbaSchedulerJobsResponse(snap.Revision, views);
    }

    private static ZbaPersonalTodosResponse BuildTodosResponse(PersonalTodoSnapshotView snap)
    {
        var items = snap.Items.Select(i => new ZbaPersonalTodoView(
            i.TodoId, i.OwnerScopeKey, i.Title, i.Notes, i.Priority, i.Status,
            i.DueUnix, i.LinkedJobId, i.SourceHint, i.CreatedUnix, i.UpdatedUnix)).ToArray();
        return new ZbaPersonalTodosResponse(snap.Revision, items);
    }
}
