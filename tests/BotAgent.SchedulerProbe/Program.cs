extern alias StorageModule;

using System.Reflection;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Identity;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Scheduler;
using BotAgent.Engine.Identity;
using BotAgent.Engine.Scheduler;
using BotAgent.Panel;
using BotAgent.Services;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Scheduler;
using BotAgent.Services.Todo;
using Microsoft.Data.Sqlite;
using AppDb = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using IdentityStore = StorageModule::BotAgent.Storage.IdentityLinkStore;
using RoleStore = StorageModule::BotAgent.Storage.AccessRoleStore;
using SchedulerStore = StorageModule::BotAgent.Storage.ScheduledJobStore;
using TodoStore = StorageModule::BotAgent.Storage.PersonalTodoStore;
using WorkspaceStore = StorageModule::BotAgent.Storage.WorkspaceConversationStore;
using KnowledgeStore = StorageModule::BotAgent.Storage.KnowledgeStore;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "botagent-sched-probe-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
Environment.SetEnvironmentVariable("QQCHAT_FILE_LOG", "0");

var passed = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException("FAILED: " + label);
    Console.WriteLine("PASS " + label);
    passed++;
}

try
{
    AppDb.Initialize(Path.Combine(root, "data", "botagent.db"));
    using (var conn = AppDb.Open())
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "PRAGMA user_version;";
        Check(Convert.ToInt64(cmd.ExecuteScalar()) == 15, "SQLite user_version remains 15 (zero schema migration)");
    }

    var tz = CronSchedule.TryResolveTimeZone("Asia/Shanghai", out var shanghaiTz)
        ? shanghaiTz
        : throw new InvalidOperationException("Asia/Shanghai timezone resolution failed");
    var baseUtc = new DateTimeOffset(2026, 11, 23, 2, 0, 0, TimeSpan.Zero); // 10:00 Asia/Shanghai
    var virtualNow = baseUtc;

    // 1. CronSchedule, security bounds & minimum 5-minute interval validation
    Check(CronSchedule.TryParse("* * * * *", out var everyMinute, out _)
        && !everyMinute!.TryValidateMinimumInterval(5, baseUtc, tz),
        "CronSchedule rejects 1-minute interval (< 5 min)");
    Check(CronSchedule.TryParse("*/2 * * * *", out var twoMinute, out _)
        && !twoMinute!.TryValidateMinimumInterval(5, baseUtc, tz),
        "CronSchedule rejects 2-minute interval (< 5 min)");
    Check(!CronSchedule.TryParse("1/2147483647 * * * *", out _, out _),
        "CronSchedule rejects integer-overflow step without crashing");
    Check(CronSchedule.TryParse("0,5,10,15,20,25,30,35,36 * * * *", out var tailDense, out _)
        && !tailDense!.TryValidateMinimumInterval(5, baseUtc, tz),
        "CronSchedule static interval validation blocks 8-occurrence bypass");
    Check(CronSchedule.TryParse("0 9 29 2 *", out var leapCron, out _)
        && leapCron!.TryValidateMinimumInterval(5, baseUtc, tz)
        && leapCron.TryGetNextOccurrence(baseUtc, tz, out var leapNext)
        && leapNext.Year == 2028 && leapNext.Month == 2 && leapNext.Day == 29,
        "CronSchedule supports Feb 29 leap-year recurrence (1466d search limit)");
    Check(!ScheduledJobValidator.ValidateJobInput("private:10001", ScheduledJobKind.CronText, "0 0 31 2 *",
        SchedulerLimits.DefaultTimeZone, """{"text":"x"}""", MissedFirePolicy.SkipAndNotify, 0, baseUtc, out _, out _, out var impossErr)
        && impossErr == "cron_no_future_occurrence",
        "Impossible calendar cron returns cron_no_future_occurrence");
    Check(!ScheduledJobValidator.ValidateJobInput("private:10001", ScheduledJobKind.OnceReminder, null,
        SchedulerLimits.DefaultTimeZone, """{"text":"x"}""", MissedFirePolicy.SkipAndNotify, baseUtc.AddMinutes(-5).ToUnixTimeSeconds(), baseUtc, out _, out _, out var pastErr)
        && pastErr == "scheduler_fire_time_in_past",
        "OnceReminder in the past is rejected with scheduler_fire_time_in_past");
    if (CronSchedule.TryResolveTimeZone("America/New_York", out var nyTz))
    {
        var dstPre = new DateTimeOffset(2026, 11, 1, 5, 10, 0, TimeSpan.Zero); // 01:10 EDT (ambiguous falls back at 02:00)
        Check(CronSchedule.TryParse("*/15 * * * *", out var dstCron, out _)
            && dstCron!.TryGetNextOccurrence(dstPre, nyTz, out var dstNext)
            && dstNext == new DateTimeOffset(2026, 11, 1, 5, 15, 0, TimeSpan.Zero),
            "CronSchedule DST fall-back picks first daylight ambiguous occurrence");
    }
    Check(CronSchedule.TryParse("*/5 * * * *", out var fiveMinute, out _)
        && fiveMinute!.TryValidateMinimumInterval(5, baseUtc, tz)
        && fiveMinute.TryGetNextOccurrence(baseUtc, tz, out var nextFive)
        && nextFive == baseUtc.AddMinutes(5),
        "CronSchedule accepts 5-minute interval and computes next occurrence");
    Check(CronSchedule.TryParse("30 9 * * 1-5", out var weekday, out _)
        && weekday!.TryValidateMinimumInterval(5, baseUtc, tz),
        "CronSchedule accepts weekday 09:30 Asia/Shanghai");

    // 2. ScheduledJobStore quotas (32 per scope / 256 global) & CRUD
    var jobStore = new SchedulerStore();
    var scopeA = "private:10001";
    for (var i = 0; i < SchedulerLimits.MaxJobsPerScope; i++)
    {
        var added = jobStore.UpsertJob(NewJob($"scopeA-{i}", scopeA, ScheduledJobKind.OnceReminder, null, baseUtc.AddHours(1).ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds()));
        if (!added.Ok) throw new InvalidOperationException($"seed scopeA-{i} failed: {added.ReasonCode}");
    }
    var overScope = jobStore.UpsertJob(NewJob("scopeA-over", scopeA, ScheduledJobKind.OnceReminder, null, baseUtc.AddHours(1).ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds()));
    Check(!overScope.Ok && overScope.ReasonCode == "scheduler_scope_quota_exceeded",
        "ScheduledJobStore enforces MaxJobsPerScope = 32");

    for (var s = 1; s < 8; s++)
    {
        var sc = $"private:{20000 + s}";
        for (var i = 0; i < SchedulerLimits.MaxJobsPerScope; i++)
        {
            var r = jobStore.UpsertJob(NewJob($"g-{s}-{i}", sc, ScheduledJobKind.OnceReminder, null, baseUtc.AddHours(2).ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds()));
            if (!r.Ok) throw new InvalidOperationException($"seed g-{s}-{i} failed: {r.ReasonCode}");
        }
    }
    var overGlobal = jobStore.UpsertJob(NewJob("global-over", "private:99999", ScheduledJobKind.OnceReminder, null, baseUtc.AddHours(2).ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds()));
    Check(!overGlobal.Ok && overGlobal.ReasonCode == "scheduler_global_quota_exceeded",
        "ScheduledJobStore enforces MaxGlobalJobs = 256");

    // Clean out synthetic quota seeds before runner tests
    foreach (var existing in jobStore.ReadSnapshot().Jobs.ToArray())
        jobStore.DeleteJob(existing.JobId);

    // 3. ScheduledJobRunner virtual-clock firing, zero-replay restart, missed-fire > 900s, QQ official block
    var delivered = new List<(string JobId, long ScheduledUnix, string? MissedNotice)>();
    var deliveryPort = new SyntheticDeliveryPort((job, schedUnix, missed) =>
    {
        delivered.Add((job.JobId, schedUnix, missed));
        return Task.FromResult(new ScheduledDeliveryResult(true, ScheduledDeliveryStatus.Succeeded, "delivered"));
    });

    var onceFire = baseUtc.AddMinutes(10);
    var cronFirst = baseUtc.AddMinutes(5);
    var missedSilentFire = baseUtc.AddMinutes(-20);   // > 900s missed
    var missedNotifyFire = baseUtc.AddMinutes(-20);   // > 900s missed
    var missed15mFire = baseUtc.AddMinutes(-20);      // > 900s missed

    jobStore.UpsertJob(NewJob("job-once", "private:10001", ScheduledJobKind.OnceReminder, null, onceFire.ToUnixTimeSeconds(), baseUtc.ToUnixTimeSeconds()));
    jobStore.UpsertJob(NewJob("job-cron", "group:10001", ScheduledJobKind.CronText, "*/5 * * * *", cronFirst.ToUnixTimeSeconds(), baseUtc.ToUnixTimeSeconds()));
    jobStore.UpsertJob(NewJob("job-official", "official:10001", ScheduledJobKind.OnceReminder, null, onceFire.ToUnixTimeSeconds(), baseUtc.ToUnixTimeSeconds()));
    jobStore.UpsertJob(NewJob("job-miss-silent", "private:10002", ScheduledJobKind.OnceReminder, null, missedSilentFire.ToUnixTimeSeconds(), baseUtc.ToUnixTimeSeconds(), MissedFirePolicy.SkipSilent));
    jobStore.UpsertJob(NewJob("job-miss-15m", "private:10003", ScheduledJobKind.OnceReminder, null, missed15mFire.ToUnixTimeSeconds(), baseUtc.ToUnixTimeSeconds(), MissedFirePolicy.FireOnce15m));
    jobStore.UpsertJob(NewJob("job-miss-notify", "private:10004", ScheduledJobKind.OnceReminder, null, missedNotifyFire.ToUnixTimeSeconds(), baseUtc.ToUnixTimeSeconds(), MissedFirePolicy.SkipAndNotify));

    using (var runner1 = new ScheduledJobRunner(jobStore, deliveryPort, () => virtualNow))
    {
        await runner1.ProcessDueAsync();
        Check(delivered.Any(d => d.JobId == "job-miss-notify" && !string.IsNullOrEmpty(d.MissedNotice)),
            "MissedFirePolicy.SkipAndNotify delivers once with missed notice");
        Check(delivered.All(d => d.JobId != "job-miss-silent" && d.JobId != "job-miss-15m"),
            "Missed > 900s skips SkipSilent and FireOnce15m");
        var runsAfterMiss = jobStore.ListRuns(limit: 20);
        Check(runsAfterMiss.Any(r => r.JobId == "job-miss-silent" && r.ReasonCode == "missed_skip_silent")
            && runsAfterMiss.Any(r => r.JobId == "job-miss-15m" && r.ReasonCode == "missed_window_expired"),
            "Skipped missed jobs record exact reason codes");

        // Advance to cronFirst (+5m) -> cron fires and advances NextFireUnix to +10m
        virtualNow = cronFirst;
        await runner1.ProcessDueAsync();
        Check(delivered.Count(d => d.JobId == "job-cron") == 1
            && jobStore.FindJob("job-cron")?.NextFireUnix == cronFirst.AddMinutes(5).ToUnixTimeSeconds(),
            "Cron job fires on due time and advances NextFireUnix");

        // Advance to onceFire (+10m) -> once fires (and disables), cron fires 2nd time, official skipped
        virtualNow = onceFire;
        await runner1.ProcessDueAsync();
        Check(delivered.Count(d => d.JobId == "job-once") == 1
            && jobStore.FindJob("job-once")?.Enabled == false
            && delivered.Count(d => d.JobId == "job-cron") == 2
            && delivered.All(d => d.JobId != "job-official"),
            "Once reminder fires once & disables; QQ official is blocked from proactive delivery");
        Check(jobStore.ListRuns("job-official").Any(r => r.ReasonCode == "passive_trigger_required"),
            "QQ official skip records passive_trigger_required");
    }

    // Restart zero-replay check with second runner instance
    var countBeforeRestart = delivered.Count;
    using (var runner2 = new ScheduledJobRunner(jobStore, deliveryPort, () => virtualNow))
    {
        await runner2.ProcessDueAsync();
        Check(delivered.Count == countBeforeRestart, "Restart at same virtual time triggers zero replays");
    }
    var directReplay = jobStore.TryClaimRunSlot("job-cron", cronFirst.ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds(), null, false, out _);
    Check(!directReplay, "Duplicate TryClaimRunSlot with same scheduled timestamp is rejected");

    // 4. Quota cutoff, paused-job race, SkipAndNotify CronTask non-execution via ScheduledDeliveryAdapter
    var box = new SettingsBox(new AppSettings { AiModeEnabled = true, WhitelistGroups = "10001" });
    var policies = new SyntheticPlatformPolicies();
    var whitelist = new WhitelistGate(box, policies);
    var fakeSource = new SyntheticQqSource();
    var plugins = new PluginManager(_ => { });
    var cutoffQuota = new SyntheticQuotaLedger(energySaving: true);
    var dispatchCalls = 0;
    var deliveryAdapter = new ScheduledDeliveryAdapter(
        box, fakeSource, policies, whitelist, plugins, cutoffQuota,
        dispatchTask: (_, _, _) => { dispatchCalls++; return Task.FromResult((true, "ok")); },
        utcNow: () => virtualNow);
    var digestJob = NewJob("job-quota", "group:10001", ScheduledJobKind.CronDigest, "*/5 * * * *", virtualNow.ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds());
    var quotaRes = await deliveryAdapter.DeliverAsync(digestJob, virtualNow.ToUnixTimeSeconds(), null, CancellationToken.None);
    Check(!quotaRes.Ok && quotaRes.Status == ScheduledDeliveryStatus.Skipped && quotaRes.ReasonCode == "quota_exceeded" && fakeSource.SendCalls == 0,
        "ScheduledDeliveryAdapter honors quota cutoff (IsEnergySaving) without calling chat transport");

    var missedTaskJob = NewJob("job-missed-task", "group:10001", ScheduledJobKind.CronTask, "*/5 * * * *", virtualNow.ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds());
    var allowDeliveryAdapter = new ScheduledDeliveryAdapter(
        box, fakeSource, policies, whitelist, plugins,
        dispatchTask: (_, _, _) => { dispatchCalls++; return Task.FromResult((true, "ok")); },
        utcNow: () => virtualNow);
    var missedTaskRes = await allowDeliveryAdapter.DeliverAsync(missedTaskJob, virtualNow.ToUnixTimeSeconds(), "missed notice", CancellationToken.None);
    Check(missedTaskRes.Ok && dispatchCalls == 0,
        "ScheduledDeliveryAdapter never dispatches CronTask when delivering SkipAndNotify missedNotice");

    var pauseTarget = NewJob("job-pause-race", "private:10001", ScheduledJobKind.CronText, "*/5 * * * *", virtualNow.AddMinutes(5).ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds());
    jobStore.UpsertJob(pauseTarget);
    jobStore.SetEnabled("job-pause-race", false, virtualNow.ToUnixTimeSeconds());
    jobStore.RecordSkippedRun("job-pause-race:" + virtualNow.AddMinutes(5).ToUnixTimeSeconds(), "job-pause-race",
        virtualNow.AddMinutes(5).ToUnixTimeSeconds(), virtualNow.ToUnixTimeSeconds(), "skip_test",
        virtualNow.AddMinutes(10).ToUnixTimeSeconds(), disableJob: false);
    Check(jobStore.FindJob("job-pause-race")?.Enabled == false,
        "RecordSkippedRun does not re-enable an already paused job");

    var onceDone = jobStore.FindJob("job-once");
    var reEnableOnce = jobStore.SetEnabled("job-once", true, virtualNow.AddMinutes(1).ToUnixTimeSeconds());
    Check(!reEnableOnce.Ok && reEnableOnce.ReasonCode == "once_reminder_already_elapsed",
        "SetEnabled refuses re-enabling an already elapsed OnceReminder");

    // 5. PersonalTodo isolation, due reminder linkage & cascade cancel
    var todoStore = new TodoStore();
    var todoPorts = new PersonalTodoHostPortsAdapter(todoStore, jobStore, utcNow: () => virtualNow);
    var groupTodo = todoPorts.Add("group:10001", "群聊非法待办", null, 2, null, null, null);
    Check(!groupTodo.Ok && groupTodo.ReasonCode == "private_conversation_only",
        "PersonalTodo rejects group conversations with private_conversation_only");

    var privateTodo = todoPorts.Add("private:10001", "带截止提醒的个人待办", "合成备注", 1, "30m", "synthetic", "Asia/Shanghai");
    Check(privateTodo.Ok && privateTodo.Item is { LinkedJobId.Length: > 0 }
        && jobStore.FindJob(privateTodo.Item.LinkedJobId!) is { Enabled: true },
        "PersonalTodo with due creates linked ScheduledJobRecord");
    var completedTodo = todoPorts.Complete("private:10001", privateTodo.Item!.TodoId);
    Check(completedTodo.Ok && jobStore.FindJob(privateTodo.Item.LinkedJobId!) is null,
        "Completing PersonalTodo cascade-deletes linked reminder job");

    var privateTodo2 = todoPorts.Add("private:10001", "删除测试待办", null, 2, "45m", null, "Asia/Shanghai");
    var delTodo = todoPorts.Delete("private:10001", privateTodo2.Item!.TodoId);
    Check(delTodo.Ok && jobStore.FindJob(privateTodo2.Item.LinkedJobId!) is null,
        "Deleting PersonalTodo cascade-deletes linked reminder job");

    // 6. ADR-0006 Optional IdentityLinkService
    var idStore = new IdentityStore();
    var idService = new IdentityLinkService(idStore, () => virtualNow);
    var epA = new IdentityEndpointKey(PlatformId.QqPrivate, AccountScope.Legacy, "", "10001");
    var epB = new IdentityEndpointKey(PlatformId.Feishu, AccountScope.Default, "feishu-1", "ou_synth_1");

    Check(!idService.IsEnabled && !idService.StartChallenge(epA).Ok,
        "ADR-0006 identity linking is disabled by default and rejects challenges");

    idService.SetEnabled(true);
    idService.UpdateSharePreferences(new IdentitySharePreferences(ShareTodos: true, ShareKnowledge: false, SharePreferences: false));

    var ch1 = idService.StartChallenge(epA);
    Check(ch1.Ok && !string.IsNullOrEmpty(ch1.ChallengeCode), "Enabled identity linking issues HMAC challenge");
    virtualNow = virtualNow.AddSeconds(121);
    var expiredConfirm = idService.ConfirmChallenge(epB, ch1.ChallengeCode!);
    Check(!expiredConfirm.Ok && expiredConfirm.ReasonCode == "challenge_invalid_or_expired",
        "HMAC challenge expires after 120 seconds");

    var ch2 = idService.StartChallenge(epA);
    var selfLink = idService.ConfirmChallenge(epA, ch2.ChallengeCode!);
    Check(!selfLink.Ok && selfLink.ReasonCode == "same_endpoint_forbidden",
        "Same-endpoint self-link is forbidden");

    var ch3 = idService.StartChallenge(epA);
    var linked = idService.ConfirmChallenge(epB, ch3.ChallengeCode!);
    var replayConfirm = idService.ConfirmChallenge(epB, ch3.ChallengeCode!);
    Check(linked.Ok && !replayConfirm.Ok && replayConfirm.ReasonCode == "challenge_invalid_or_expired",
        "Valid cross-platform challenge confirms once and rejects single-use replay");

    var sharedA = idService.ResolveSharedOwnerScope(epA, IdentityResourceKind.Todos);
    var sharedB = idService.ResolveSharedOwnerScope(epB, IdentityResourceKind.Todos);
    Check(sharedA is not null && sharedA == sharedB
        && idService.ResolveSharedOwnerScope(epA, IdentityResourceKind.Knowledge) is null,
        "Linked endpoints share Todos only when ShareTodos=true and respect ShareKnowledge=false");

    Check(idService.ResolveWorkspaceSharedScope(IdentityResourceKind.Todos) is null,
        "ResolveWorkspaceSharedScope requires explicit workspace binding (never infers groups.Length == 1)");
    var wsBind = idService.BindWorkspace(linked.CanonicalPersonId);
    Check(wsBind.Ok && idService.ResolveWorkspaceSharedScope(IdentityResourceKind.Todos) == linked.CanonicalPersonId,
        "BindWorkspace activates shared workspace scope after explicit confirmation");

    var chPurge = idService.StartChallenge(epA);
    var roleStore = new RoleStore();
    Check(roleStore.Load().Bindings.Length == 0, "Identity linking never creates or mutates RBAC role bindings");

    var unlinked = idService.Unlink(epA);
    var postUnlinkConfirm = idService.ConfirmChallenge(epB, chPurge.ChallengeCode!);
    Check(unlinked.Ok && idService.ResolveSharedOwnerScope(epA, IdentityResourceKind.Todos) is null
        && idService.ResolveSharedOwnerScope(epB, IdentityResourceKind.Todos) is null
        && idService.ResolveWorkspaceSharedScope(IdentityResourceKind.Todos) is null
        && !postUnlinkConfirm.Ok,
        "Unlink immediately revokes shared owner/workspace scope and purges pending challenges");

    // 7. WorkspaceConversationStore & PluginUiValidation fail-closed allowlist
    var wsStore = new WorkspaceStore();
    for (var i = 0; i < 205; i++)
        wsStore.Append("user", $"synthetic-ws-msg-{i}", "chat", virtualNow.ToUnixTimeSeconds());
    var wsSnap = wsStore.ReadAfter(0, 200);
    Check(wsSnap.LastSequence == 205 && wsSnap.Messages.Count == 200 && wsSnap.Messages[0].Sequence == 6,
        "WorkspaceConversationStore preserves workspace:owner stream bounded to 200 items");

    var uiValType = typeof(ScheduledDeliveryAdapter).Assembly.GetType("BotAgent.Adapters.Plugins.PluginUiValidation")
        ?? throw new InvalidOperationException("PluginUiValidation type not found");
    var isActionAllowed = uiValType.GetMethod("IsActionAllowedForPlugin", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("IsActionAllowedForPlugin not found");
    bool Allowed(string pluginId, string action) => (bool)isActionAllowed.Invoke(null, [pluginId, action])!;

    Check(Allowed("preset.feature.scheduler", "scheduler.list")
        && Allowed("preset.feature.scheduler", "scheduler.save")
        && !Allowed("preset.feature.scheduler", "quotas.list")
        && !Allowed("preset.feature.other", "scheduler.list")
        && Allowed("preset.feature.personal-todo", "todo.save")
        && !Allowed("preset.feature.personal-todo", "todo.add")
        && !Allowed("preset.feature.other", "todo.list")
        && !Allowed("preset.feature.other", "workspace.messages.read"),
        "PluginUiValidation enforces fail-closed UI action allowlist");

    Check(SchedulerHttpContract.Describe().Routes.All(r => r.Path.StartsWith("/zba/v1/scheduler/", StringComparison.Ordinal))
        && PersonalTodoHttpContract.Describe().Routes.All(r => r.Path.StartsWith("/zba/v1/todos", StringComparison.Ordinal)),
        "Scheduler and PersonalTodo HTTP contracts stay strictly within /zba/v1/scheduler/* and /zba/v1/todos*");

    // 8. Total Review regressions: PersonalTodo canonical feature key & fail-closed IdentityLink fallback
    Check(EffectivePlatformPolicy.FeatureKey(PlatformFeatureId.PersonalTodo) == "personal_todo",
        "EffectivePlatformPolicy.FeatureKey maps PersonalTodo to canonical personal_todo key");
    var policyBan = new EffectivePlatformPolicy(
        new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
        true, true, true, true, PlatformCapabilities.QqOneBot,
        new HashSet<string>(),
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [EffectivePlatformPolicy.FeatureKey(PlatformFeatureId.PersonalTodo)] = false
        },
        Array.Empty<string>());
    Check(!policyBan.Feature("personal_todo").Enabled
        && !ConversationProfileResolver.Resolve(new AppSettings(), policyBan, "private:10001").FeatureEnabled("personal_todo"),
        "Platform false on PersonalTodo blocks personal_todo across policy & conversation profile resolution");

    // Unattested string-only PersonalTodo scope must NEVER resolve to shared:<id>
    idService.SetEnabled(true);
    idService.UpdateSharePreferences(new IdentitySharePreferences(ShareTodos: true, ShareKnowledge: true, SharePreferences: false));
    var linkChallenge = idService.StartChallenge(epA);
    idService.ConfirmChallenge(epB, linkChallenge.ChallengeCode!);
    var unattestedTodoPorts = new PersonalTodoHostPortsAdapter(todoStore, jobStore, idService, () => virtualNow);
    Check(unattestedTodoPorts.TryResolveAuthorizedOwnerScope("private:10001", out var unattestedOwnerScope, out _)
        && !unattestedOwnerScope.StartsWith("shared:", StringComparison.Ordinal),
        "PersonalTodoHostPortsAdapter never maps unattested string-only sourceKey to shared scope");

    // KnowledgeStore per-scope byte cap
    StorageModule::BotAgent.Storage.StorageClock.Initialize(new BotAgent.Adapters.Time.SystemClock());
    var kbStore = new KnowledgeStore();
    var kbLarge = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"第{i}段合成测试条目 " + new string('k', 900)));
    var kbSaved = true;
    for (var d = 0; d < 25; d++)
    {
        var r = kbStore.SaveDocument("scope:cap:test", $"Doc-{d}", kbLarge, "note", new BotAgent.Services.Knowledge.KnowledgeOptions(256, 4096, 1600));
        if (!r.Ok && r.ReasonCode == "scope_payload_too_large")
        {
            kbSaved = false;
            break;
        }
    }
    Check(!kbSaved, "KnowledgeStore enforces per-scope serialized payload cap (scope_payload_too_large)");

    // Real ZbaHostBackend.BuildSchedulerJobsResponse redaction even when AgentMaskSensitive=false
    var upsertRedact = jobStore.UpsertJob(NewJob(
        "zba-redact-test",
        "private:10001",
        ScheduledJobKind.OnceReminder,
        null,
        virtualNow.AddMinutes(30).ToUnixTimeSeconds(),
        virtualNow.ToUnixTimeSeconds()));
    Check(upsertRedact.Ok, "ScheduledJobStore upserts private job for ZbaHostBackend redaction test");
    var zbaBackendType = typeof(BotAgent.Services.Scheduler.ScheduledDeliveryAdapter).Assembly
        .GetType("BotAgent.Adapters.Panel.Zba.ZbaHostBackend", throwOnError: true)!;
    var buildJobsMethod = zbaBackendType.GetMethod(
        "BuildSchedulerJobsResponse",
        BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildSchedulerJobsResponse not found");
    var snapForZba = jobStore.ReadSnapshot();
    var readView = (BotAgent.Panel.ZbaSchedulerJobsResponse)buildJobsMethod.Invoke(null, [snapForZba, false, false])!;
    var adminView = (BotAgent.Panel.ZbaSchedulerJobsResponse)buildJobsMethod.Invoke(null, [snapForZba, false, true])!;
    var readItem = readView.Jobs.First(j => j.JobId == "zba-redact-test");
    var adminItem = adminView.Jobs.First(j => j.JobId == "zba-redact-test");
    Check(readItem.Text == "[受保护]" && readItem.ScopeKey != "private:10001",
        "Real ZbaHostBackend redacts scheduler text and scopeKey for read-only callers even when maskSensitive=false");
    Check(adminItem.Text == "synthetic reminder zba-redact-test" && adminItem.ScopeKey == "private:10001",
        "Real ZbaHostBackend preserves scheduler text and raw scopeKey for admin callers when maskSensitive=false");

    Console.WriteLine($"BotAgent.SchedulerProbe: {passed} passed, 0 failed");
    return 0;
}
finally
{
    SqliteConnection.ClearAllPools();
    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
}

static ScheduledJobRecord NewJob(
    string id,
    string scopeKey,
    string kind,
    string? cronExpr,
    long nextFireUnix,
    long nowUnix,
    string missedPolicy = MissedFirePolicy.SkipAndNotify)
    => new(
        JobId: id,
        ScopeKey: scopeKey,
        CreatorUserId: "synthetic",
        Kind: kind,
        CronExpr: cronExpr,
        TimezoneIana: SchedulerLimits.DefaultTimeZone,
        PayloadJson: JsonSerializer.Serialize(new { text = "synthetic reminder " + id }),
        MissedPolicy: missedPolicy,
        Enabled: true,
        NextFireUnix: nextFireUnix,
        LastFiredUnix: null,
        FailureCount: 0,
        CreatedUnix: nowUnix,
        UpdatedUnix: nowUnix);

file sealed class SyntheticDeliveryPort(Func<ScheduledJobRecord, long, string?, Task<ScheduledDeliveryResult>> onDeliver)
    : IScheduledDeliveryPort
{
    public Task<ScheduledDeliveryResult> DeliverAsync(ScheduledJobRecord job, long scheduledUnix, string? missedNotice, CancellationToken ct)
        => onDeliver(job, scheduledUnix, missedNotice);
}

file sealed class SyntheticPlatformPolicies : IPlatformPolicyResolver
{
    public EffectivePlatformPolicy Resolve(PlatformContext context) => Allow(context);
    public EffectivePlatformPolicy ResolveForChannel(string? channel) => Allow(new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy));
    public bool IsChatEnabled(string? channel) => true;
    private static EffectivePlatformPolicy Allow(PlatformContext ctx)
        => new(ctx, true, true, true, true, PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>());
}

file sealed class SyntheticQuotaLedger(bool energySaving) : ITenantQuotaLedger
{
    public TenantQuotaSnapshot GetQuota(string tenantId) => new(tenantId, 100, energySaving ? 100 : 0, 0, "2026-11-23", energySaving);
    public TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit) => GetQuota(tenantId);
    public bool IsEnergySaving(string tenantId) => energySaving;
    public void RecordUsage(string tenantId, int promptTokens, int completionTokens) { }
}

file sealed class SyntheticQqSource : IQqChatSource
{
    public int SendCalls { get; private set; }
    public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
    {
        SendCalls++;
        return Task.FromResult(new SendResult(true));
    }
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));
    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Task.FromResult(true);
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Task.FromResult<string?>("synthetic-group");
}
