using System.Reflection;
using System.Text;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Scheduler;
using BotAgent.Domain.Tools;
using BotAgent.Services.Scheduler;
using BotAgent.Services.Tools;

namespace BotAgent.Packages.Scheduler;

internal sealed class SchedulerCoordinator(ISchedulerHostPorts? ports, Action<string> log) : ISchedulerContribution
{
    public bool IsReady => ports is not null;

    public SchedulerMutationResult CreateReminder(string sourceKey, string creatorUserId, string whenText, string messageText, string? timezoneIana)
        => ports is null
            ? new(false, false, "scheduler_unavailable")
            : ports.CreateReminder(sourceKey, creatorUserId, whenText, messageText, timezoneIana);

    public ScheduledJobRecord[] ListReminders(string sourceKey)
        => ports is null ? [] : ports.ListReminders(sourceKey);

    public SchedulerMutationResult SetReminderEnabled(string sourceKey, string jobId, bool enabled)
        => ports is null
            ? new(false, false, "scheduler_unavailable")
            : ports.SetReminderEnabled(sourceKey, jobId, enabled);

    public SchedulerMutationResult DeleteReminder(string sourceKey, string jobId)
        => ports is null
            ? new(false, false, "scheduler_unavailable")
            : ports.DeleteReminder(sourceKey, jobId);

    internal ToolOutcome ExecuteCreate(string sourceKey, string whenText, string messageText, string? timezoneIana)
    {
        if (ports is null) return ToolOutcome.Failure("scheduler_unavailable", "");
        var res = ports.CreateReminder(sourceKey, "tool", whenText, messageText, timezoneIana);
        if (!res.Ok || res.Job is null) return ToolOutcome.Failure(res.ReasonCode, "");
        CronSchedule.TryResolveTimeZone(res.Job.TimezoneIana, out var tz);
        var localFire = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(res.Job.NextFireUnix), tz);
        log($"[Scheduler] tool created {res.Job.JobId}");
        return ToolOutcome.Success(
            $"已创建提醒 {res.Job.JobId}：将于 {localFire:yyyy-MM-dd HH:mm:ss}（{res.Job.TimezoneIana}）触发。",
            $"jobId={res.Job.JobId}");
    }

    internal ToolOutcome ExecuteList(string sourceKey)
    {
        if (ports is null) return ToolOutcome.Failure("scheduler_unavailable", "");
        var jobs = ports.ListReminders(sourceKey);
        if (jobs.Length == 0)
            return ToolOutcome.Success("[定时提醒清单]\n当前会话暂无提醒任务。", "jobs=0");
        var sb = new StringBuilder();
        sb.AppendLine($"[定时提醒清单 | 共 {jobs.Length} 条]");
        foreach (var j in jobs.Take(20))
        {
            CronSchedule.TryResolveTimeZone(j.TimezoneIana, out var tz);
            var localFire = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(j.NextFireUnix), tz);
            sb.AppendLine($"- {j.JobId} | {j.Kind} | {(j.Enabled ? "启用" : "暂停")} | {localFire:yyyy-MM-dd HH:mm} ({j.TimezoneIana})");
        }
        return ToolOutcome.Success(sb.ToString().TrimEnd(), $"jobs={jobs.Length}");
    }

    internal ToolOutcome ExecutePause(string sourceKey, string jobId)
    {
        if (ports is null) return ToolOutcome.Failure("scheduler_unavailable", "");
        var res = ports.SetReminderEnabled(sourceKey, jobId, false);
        return res.Ok
            ? ToolOutcome.Success($"已暂停提醒任务：{jobId}", $"paused={jobId}")
            : ToolOutcome.Failure(res.ReasonCode, "");
    }

    internal ToolOutcome ExecuteDelete(string sourceKey, string jobId)
    {
        if (ports is null) return ToolOutcome.Failure("scheduler_unavailable", "");
        var res = ports.DeleteReminder(sourceKey, jobId);
        return res.Ok
            ? ToolOutcome.Success($"已删除提醒任务：{jobId}", $"deleted={jobId}")
            : ToolOutcome.Failure(res.ReasonCode, "");
    }
}

internal sealed class SchedulerTools(SchedulerCoordinator coordinator) : IToolContribution
{
    public ToolDefinition[] DescribeTools() =>
    [
        new(new("reminder.list", ToolCategory.ConversationRead, "列出当前会话的定时与提醒任务", true, "scheduler.actions",
            "", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.Scheduler, Origin: ToolOrigin.Plugin),
            []),
        new(new("reminder.create", ToolCategory.ConversationRead, "在当前会话创建定时提醒（需明确绝对时间与时区）", false, "scheduler.actions",
            "when, text, timezone?", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.Scheduler, Origin: ToolOrigin.Plugin, PromptVisible: false),
            [
                new("when", Required: true, MinLength: 1, MaxLength: 64),
                new("text", Required: true, MinLength: 1, MaxLength: 500),
                new("timezone", Required: false, MinLength: 1, MaxLength: 64)
            ]),
        new(new("reminder.pause", ToolCategory.ConversationRead, "暂停当前会话的指定提醒任务", false, "scheduler.actions",
            "jobId", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.Scheduler, Origin: ToolOrigin.Plugin, PromptVisible: false),
            [new("jobId", Required: true, MinLength: 3, MaxLength: 64)]),
        new(new("reminder.delete", ToolCategory.ConversationRead, "删除当前会话的指定提醒任务", false, "scheduler.actions",
            "jobId", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.Scheduler, Origin: ToolOrigin.Plugin, PromptVisible: false),
            [new("jobId", Required: true, MinLength: 3, MaxLength: 64)])
    ];

    public Task<ToolOutcome> ExecuteAsync(string sourceKey, ToolCall call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var definition = DescribeTools().SingleOrDefault(t => t.Spec.Id == call.ToolId);
        if (definition is null) return Task.FromResult(ToolOutcome.Failure("unknown_tool", ""));
        var verdict = ToolArguments.Validate(definition, call.Arguments);
        if (!verdict.Ok) return Task.FromResult(ToolOutcome.Failure(verdict.ReasonCode, ""));
        if (string.IsNullOrWhiteSpace(sourceKey)) return Task.FromResult(ToolOutcome.Failure("source_required", ""));

        var args = call.Arguments!;
        var result = call.ToolId switch
        {
            "reminder.list" => coordinator.ExecuteList(sourceKey),
            "reminder.create" => coordinator.ExecuteCreate(
                sourceKey,
                args["when"]!.GetValue<string>().Trim(),
                args["text"]!.GetValue<string>().Trim(),
                args.TryGetPropertyValue("timezone", out var tzNode) && tzNode is not null ? tzNode.ToString().Trim() : null),
            "reminder.pause" => coordinator.ExecutePause(sourceKey, args["jobId"]!.GetValue<string>().Trim()),
            "reminder.delete" => coordinator.ExecuteDelete(sourceKey, args["jobId"]!.GetValue<string>().Trim()),
            _ => ToolOutcome.Failure("unknown_tool", "")
        };
        return Task.FromResult(result);
    }
}

public sealed class SchedulerPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private SchedulerCoordinator? _coordinator;
    private SchedulerTools? _tools;

    public string Id => "preset.feature.scheduler";
    public string Name => "定时与提醒引擎";
    public string Version => typeof(SchedulerPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "持久化多平台一次性提醒、周期 Cron 推送与防重启重放引擎，遵守通道策略、白名单与配额约束。";
    public PluginCategory Category => PluginCategory.Feature;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ports = context.Services.GetService(typeof(ISchedulerHostPorts)) as ISchedulerHostPorts;
        var log = context.Services.GetService(typeof(Action<string>)) as Action<string> ?? (_ => { });
        _coordinator = new SchedulerCoordinator(ports, log);
        _tools = new SchedulerTools(_coordinator);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(ISchedulerContribution) ? _coordinator
            : contract == typeof(IToolContribution) ? _tools
            : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _coordinator = null;
        _tools = null;
        return Task.CompletedTask;
    }
}
