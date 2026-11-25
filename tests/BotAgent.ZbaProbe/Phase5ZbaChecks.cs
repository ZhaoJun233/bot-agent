using System.Text;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Scheduler;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

internal static class Phase5ZbaChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var jobs = new List<ZbaScheduledJobView>();
        var runs = new List<ZbaScheduledJobRunView>
        {
            new("job-1:1700000300", "job-1", 1700000300, 1700000300, 1700000301, ScheduledDeliveryStatus.Succeeded, "delivered")
        };
        var todos = new List<ZbaPersonalTodoView>();
        var messages = new List<ZbaWorkspaceMessageView>();
        var identityLinks = new List<ZbaWorkspaceIdentityLinkView>();
        long jobRev = 0;
        long todoRev = 0;
        long wsSeq = 0;
        var identityEnabled = false;
        var shareTodos = false;
        var shareKnowledge = false;
        var sharePrefs = false;
        SyntheticZbaBackend? backendRef = null;

        var schedulerBinding = SchedulerHttpContract.Bind<ConfigurationCaller>(
            (caller, ct) =>
            {
                var isAdmin = (caller.Access & ConfigurationAccess.SaveConfiguration) != 0;
                var list = isAdmin
                    ? jobs.ToArray()
                    : jobs.Select(j => j with { ScopeKey = j.MaskedScope, Text = "[受保护]" }).ToArray();
                return ValueTask.FromResult(new ZbaSchedulerJobsResponse(jobRev, list));
            },
            (caller, req, ct) =>
            {
                if (req.ExpectedRevision != jobRev)
                    throw new PluginHttpException(409, "scheduler_revision_conflict");
                if (ScheduledJobValidator.IsQqOfficialScope(req.ScopeKey))
                    throw new PluginHttpException(400, "passive_trigger_required");
                if (!ScheduledJobValidator.ValidateJobInput(req.ScopeKey, req.Kind, req.CronExpr, req.TimezoneIana,
                    JsonSerializer.Serialize(new { text = req.Text }), req.MissedPolicy ?? MissedFirePolicy.SkipAndNotify,
                    req.NextFireUnix, Program.FixedNow, out _, out var resolvedNext, out var err))
                {
                    throw new PluginHttpException(400, err);
                }

                jobRev++;
                var id = string.IsNullOrWhiteSpace(req.JobId) ? "job-" + jobRev : req.JobId.Trim();
                jobs.RemoveAll(j => j.JobId == id);
                jobs.Add(new ZbaScheduledJobView(
                    id, req.ScopeKey, "pr***01", req.Kind, req.CronExpr,
                    req.TimezoneIana ?? SchedulerLimits.DefaultTimeZone,
                    req.Text, req.MissedPolicy ?? MissedFirePolicy.SkipAndNotify,
                    req.Enabled, resolvedNext, null, 0));
                return ValueTask.FromResult(new ZbaSchedulerWriteResult(true, false, "created", new ZbaSchedulerJobsResponse(jobRev, jobs.ToArray())));
            },
            (caller, req, ct) =>
            {
                if (req.ExpectedRevision != jobRev)
                    throw new PluginHttpException(409, "scheduler_revision_conflict");
                jobRev++;
                jobs.RemoveAll(j => j.JobId == req.JobId);
                return ValueTask.FromResult(new ZbaSchedulerWriteResult(true, false, "deleted", new ZbaSchedulerJobsResponse(jobRev, jobs.ToArray())));
            },
            (caller, req, ct) => ValueTask.FromResult(new ZbaSchedulerRunsResponse(
                string.IsNullOrWhiteSpace(req.JobId)
                    ? runs.ToArray()
                    : runs.Where(r => r.JobId == req.JobId).ToArray())));

        var todoBinding = PersonalTodoHttpContract.Bind<ConfigurationCaller>(
            (caller, ct) => ValueTask.FromResult(new ZbaPersonalTodosResponse(todoRev, todos.ToArray())),
            (caller, req, ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.Title))
                    throw new PluginHttpException(400, "todo_title_invalid");
                if (req.ExpectedRevision != todoRev)
                    throw new PluginHttpException(409, "todo_revision_conflict");
                todoRev++;
                var id = string.IsNullOrWhiteSpace(req.TodoId) ? "todo-" + todoRev : req.TodoId.Trim();
                todos.RemoveAll(t => t.TodoId == id);
                todos.Add(new ZbaPersonalTodoView(id, SchedulerLimits.WorkspaceOwnerScopeKey, req.Title.Trim(),
                    req.Notes, Math.Clamp(req.Priority, 1, 3), req.Status, req.DueUnix, null, req.SourceHint, 1700000000, 1700000000));
                return ValueTask.FromResult(new ZbaPersonalTodoWriteResult(true, false, "created", new ZbaPersonalTodosResponse(todoRev, todos.ToArray())));
            },
            (caller, req, ct) =>
            {
                if (req.ExpectedRevision != todoRev)
                    throw new PluginHttpException(409, "todo_revision_conflict");
                todoRev++;
                todos.RemoveAll(t => t.TodoId == req.TodoId);
                return ValueTask.FromResult(new ZbaPersonalTodoWriteResult(true, false, "deleted", new ZbaPersonalTodosResponse(todoRev, todos.ToArray())));
            });

        var workspaceBinding = WorkspaceHttpContract.Bind<ConfigurationCaller>(
            (caller, ct) => ValueTask.FromResult(new ZbaWorkspaceMessagesResponse(SchedulerLimits.WorkspaceOwnerScopeKey, wsSeq, messages.ToArray())),
            (caller, req, ct) =>
            {
                if (!string.IsNullOrWhiteSpace(req.AttachmentBase64))
                {
                    byte[] raw;
                    try { raw = Convert.FromBase64String(req.AttachmentBase64); }
                    catch (FormatException) { throw new PluginHttpException(400, "attachment_invalid_base64"); }
                    if (raw.Length == 0 || raw.Length > 10 * 1024 * 1024)
                        throw new PluginHttpException(400, "attachment_too_large");
                }
                if (string.IsNullOrWhiteSpace(req.Text) && string.IsNullOrWhiteSpace(req.AttachmentBase64))
                    throw new PluginHttpException(400, "workspace_text_required");

                var msg = new ZbaWorkspaceMessageView(++wsSeq, "user", req.Text, req.Mode, req.AttachmentName, 1700000000, null, "sent");
                messages.Add(msg);
                backendRef!.ExtraEvents.Add(new ZbaEvent(
                    wsSeq, "workspace.message.appended", Program.FixedNow, "workspace-message-" + wsSeq, "sent", null, null));
                return ValueTask.FromResult(new ZbaWorkspaceSendResult(true, false, req.IdempotencyKey, "sent", null,
                    new ZbaWorkspaceMessagesResponse(SchedulerLimits.WorkspaceOwnerScopeKey, wsSeq, messages.ToArray())));
            },
            (caller, ct) =>
            {
                messages.Clear();
                return ValueTask.FromResult(new ZbaWorkspaceClearResult(true,
                    new ZbaWorkspaceMessagesResponse(SchedulerLimits.WorkspaceOwnerScopeKey, wsSeq, messages.ToArray())));
            },
            (caller, ct) => ValueTask.FromResult(new ZbaWorkspaceIdentityResponse(identityEnabled, shareTodos, shareKnowledge, sharePrefs, identityLinks.ToArray())),
            (caller, req, ct) =>
            {
                identityEnabled = req.Enabled;
                shareTodos = req.ShareTodos;
                shareKnowledge = req.ShareKnowledge;
                sharePrefs = req.SharePreferences;
                return ValueTask.FromResult(new ZbaWorkspaceIdentityResponse(identityEnabled, shareTodos, shareKnowledge, sharePrefs, identityLinks.ToArray()));
            });

        var backend = new SyntheticZbaBackend
        {
            CustomHttpContributions = [schedulerBinding, todoBinding, workspaceBinding]
        };
        backendRef = backend;
        var endpoint = new ZbaEndpoint(
            backend,
            new ZbaOptions { AdminToken = "p5-admin", ReadToken = "p5-read" },
            () => Program.FixedNow, "synthetic", "p5-inst");

        async Task<ZbaResponse> Send(string method, string path, string token, string body = "", string query = "")
            => await endpoint.ProcessAsync(new(method, path, query,
                new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                Encoding.UTF8.GetBytes(body), "203.0.113.31"));

        var openapi = JsonDocument.Parse((await Send("GET", "/zba/v1/openapi.json", "p5-read")).Body).RootElement.GetProperty("paths");
        foreach (var route in new[]
        {
            "/zba/v1/scheduler/jobs", "/zba/v1/scheduler/save", "/zba/v1/scheduler/delete", "/zba/v1/scheduler/runs",
            "/zba/v1/todos", "/zba/v1/todos/save", "/zba/v1/todos/delete",
            "/zba/v1/workspace/messages", "/zba/v1/workspace/messages/send", "/zba/v1/workspace/messages/clear",
            "/zba/v1/workspace/identity", "/zba/v1/workspace/identity/toggle"
        })
        {
            check(openapi.TryGetProperty(route, out _), "P5 OpenAPI declares " + route);
        }

        // Method-not-allowed (405) checks
        check((await Send("POST", "/zba/v1/scheduler/jobs", "p5-admin")).Status == 405, "P5 405 on POST /zba/v1/scheduler/jobs");
        check((await Send("GET", "/zba/v1/scheduler/save", "p5-admin")).Status == 405, "P5 405 on GET /zba/v1/scheduler/save");
        check((await Send("GET", "/zba/v1/todos/save", "p5-admin")).Status == 405, "P5 405 on GET /zba/v1/todos/save");
        check((await Send("POST", "/zba/v1/workspace/messages", "p5-admin")).Status == 405, "P5 405 on POST /zba/v1/workspace/messages");
        check((await Send("GET", "/zba/v1/workspace/messages/send", "p5-admin")).Status == 405, "P5 405 on GET /zba/v1/workspace/messages/send");

        // Scheduler auth + validation + ReadToken redaction
        var jobsReadEmpty = await Send("GET", "/zba/v1/scheduler/jobs", "p5-read");
        check(jobsReadEmpty.Status == 200, "P5 read token can list scheduler jobs");
        check((await Send("POST", "/zba/v1/scheduler/save", "p5-read", """{"expectedRevision":0,"idempotencyKey":"k1","scopeKey":"workspace:owner","kind":"once_reminder","text":"x"}""")).Status == 403,
            "P5 read token blocked on POST /zba/v1/scheduler/save");

        var officialReject = await Send("POST", "/zba/v1/scheduler/save", "p5-admin",
            """{"expectedRevision":0,"idempotencyKey":"k-off","scopeKey":"official:10001","kind":"once_reminder","text":"x","nextFireUnix":1900000000}""");
        check(officialReject.Status == 400 && Encoding.UTF8.GetString(officialReject.Body).Contains("passive_trigger_required"),
            "P5 /zba/v1/scheduler/save rejects QQ official passive scope");

        var saveJob = await Send("POST", "/zba/v1/scheduler/save", "p5-admin",
            """{"expectedRevision":0,"idempotencyKey":"k-job-1","scopeKey":"private:10001","kind":"cron_text","cronExpr":"*/5 * * * *","timezoneIana":"Asia/Shanghai","text":"喝水提醒"}""");
        check(saveJob.Status == 200 && Encoding.UTF8.GetString(saveJob.Body).Contains("job-1"),
            "P5 /zba/v1/scheduler/save persists cron job");
        var jobsRead = await Send("GET", "/zba/v1/scheduler/jobs", "p5-read");
        var jobsReadParsed = JsonSerializer.Deserialize<ZbaSchedulerJobsResponse>(
            jobsRead.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        check(jobsRead.Status == 200
            && jobsReadParsed is { Jobs.Count: > 0 }
            && jobsReadParsed.Jobs.All(j => j.Text == "[受保护]" && j.ScopeKey == "pr***01"),
            "P5 read token redacts persisted scheduler job text and scopeKey");
        var conflictJob = await Send("POST", "/zba/v1/scheduler/save", "p5-admin",
            """{"expectedRevision":0,"idempotencyKey":"k-job-stale","scopeKey":"private:10001","kind":"cron_text","cronExpr":"*/5 * * * *","text":"stale"}""");
        check(conflictJob.Status == 409, "P5 /zba/v1/scheduler/save rejects stale expectedRevision (409)");

        var missingRequired = await Send("POST", "/zba/v1/scheduler/save", "p5-admin",
            """{"idempotencyKey":"k-miss","scopeKey":"private:10001","kind":"cron_text"}""");
        check(missingRequired.Status == 400, "P5 /zba/v1/scheduler/save rejects missing [JsonRequired] fields");

        var oversizeOtherRoute = await Send("POST", "/zba/v1/scheduler/save", "p5-admin",
            JsonSerializer.Serialize(new { expectedRevision = 0, idempotencyKey = "k-huge", scopeKey = "private:10001", kind = "cron_text", cronExpr = "*/5 * * * *", text = new string('x', 1_200_000) }));
        check(oversizeOtherRoute.Status == 413, "P5 non-workspace routes enforce default 1 MiB body limit (413)");

        var runsRes = await Send("POST", "/zba/v1/scheduler/runs", "p5-read", """{"jobId":"job-1","limit":10}""");
        check(runsRes.Status == 200 && Encoding.UTF8.GetString(runsRes.Body).Contains("job-1:1700000300"),
            "P5 /zba/v1/scheduler/runs returns bounded run ledger");

        // Personal Todo auth + CRUD
        check((await Send("GET", "/zba/v1/todos", "p5-read")).Status == 403, "P5 read token blocked on GET /zba/v1/todos (403)");
        check((await Send("GET", "/zba/v1/todos", "p5-admin")).Status == 200, "P5 admin token can list todos");
        check((await Send("POST", "/zba/v1/todos/save", "p5-read", """{"expectedRevision":0,"idempotencyKey":"t1","title":"x"}""")).Status == 403,
            "P5 read token blocked on /zba/v1/todos/save");
        var saveTodo = await Send("POST", "/zba/v1/todos/save", "p5-admin",
            """{"expectedRevision":0,"idempotencyKey":"t-save-1","title":"合成测试待办","priority":1}""");
        check(saveTodo.Status == 200 && Encoding.UTF8.GetString(saveTodo.Body).Contains("todo-1"),
            "P5 /zba/v1/todos/save persists todo");
        var delTodo = await Send("POST", "/zba/v1/todos/delete", "p5-admin",
            """{"expectedRevision":1,"idempotencyKey":"t-del-1","todoId":"todo-1"}""");
        check(delTodo.Status == 200, "P5 /zba/v1/todos/delete removes todo");

        // Workspace admin-only privacy & zero-body events + >1 MiB per-route attachment limit
        check((await Send("GET", "/zba/v1/workspace/messages", "p5-read")).Status == 403,
            "P5 GET /zba/v1/workspace/messages requires admin token (403 on read token)");
        check((await Send("POST", "/zba/v1/workspace/messages/send", "p5-read", """{"idempotencyKey":"w1","text":"hi"}""")).Status == 403,
            "P5 POST /zba/v1/workspace/messages/send requires admin token");

        var largeValidB64 = Convert.ToBase64String(new byte[1_500_000]);
        var largeOk = await Send("POST", "/zba/v1/workspace/messages/send", "p5-admin",
            JsonSerializer.Serialize(new { idempotencyKey = "ws-1_5m-ok", text = "1.5MiB", mode = "chat", attachmentName = "large.txt", attachmentBase64 = largeValidB64 }));
        check(largeOk.Status == 200, "P5 /zba/v1/workspace/messages/send accepts >1 MiB attachment under default ZbaOptions via per-route ceiling");

        var badBase64 = await Send("POST", "/zba/v1/workspace/messages/send", "p5-admin",
            """{"idempotencyKey":"ws-bad-b64","text":"bad","mode":"chat","attachmentName":"a.txt","attachmentBase64":"%%%not-base64%%%"}""");
        check(badBase64.Status == 400 && Encoding.UTF8.GetString(badBase64.Body).Contains("attachment_invalid_base64"),
            "P5 /zba/v1/workspace/messages/send rejects invalid base64 attachment");

        var oversizedB64 = Convert.ToBase64String(new byte[10 * 1024 * 1024 + 1]);
        var bigRes = await Send("POST", "/zba/v1/workspace/messages/send", "p5-admin",
            JsonSerializer.Serialize(new { idempotencyKey = "ws-big-1", text = "big", mode = "chat", attachmentName = "big.bin", attachmentBase64 = oversizedB64 }));
        check(bigRes.Status == 400 && Encoding.UTF8.GetString(bigRes.Body).Contains("attachment_too_large"),
            "P5 /zba/v1/workspace/messages/send rejects >10 MiB attachment");

        var secretText = "SYNTHETIC_WORKSPACE_PRIVATE_BODY_NEVER_IN_EVENTS";
        var sentWs = await Send("POST", "/zba/v1/workspace/messages/send", "p5-admin",
            JsonSerializer.Serialize(new { idempotencyKey = "ws-ok-1", text = secretText, mode = "chat" }));
        check(sentWs.Status == 200 && Encoding.UTF8.GetString(sentWs.Body).Contains(secretText),
            "P5 /zba/v1/workspace/messages/send persists owner message");

        var eventsRes = await Send("GET", "/zba/v1/events", "p5-read", "", "after=0");
        var eventsBody = Encoding.UTF8.GetString(eventsRes.Body);
        check(eventsRes.Status == 200
            && eventsBody.Contains("workspace.message.appended")
            && !eventsBody.Contains(secretText),
            "P5 workspace.message.appended event contains zero message body text");

        var idToggleRead = await Send("POST", "/zba/v1/workspace/identity/toggle", "p5-read",
            """{"enabled":true,"shareTodos":true,"shareKnowledge":false,"sharePreferences":false}""");
        check(idToggleRead.Status == 403, "P5 /zba/v1/workspace/identity/toggle blocked on read token");
        var idToggleAdmin = await Send("POST", "/zba/v1/workspace/identity/toggle", "p5-admin",
            """{"enabled":true,"shareTodos":true,"shareKnowledge":false,"sharePreferences":false}""");
        check(idToggleAdmin.Status == 200 && JsonDocument.Parse(idToggleAdmin.Body).RootElement.GetProperty("shareTodos").GetBoolean(),
            "P5 /zba/v1/workspace/identity/toggle updates share preferences");
    }
}
