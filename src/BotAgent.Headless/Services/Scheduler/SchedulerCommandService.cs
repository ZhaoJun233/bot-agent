using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Scheduler;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Scheduler;

/// <summary>
/// 确定性 <c>//remind</c> 定时提醒命令处理器：支持 add / list / pause / resume / delete。
/// 诚实暴露 QQ 官方通道被动边界（<c>passive_trigger_required</c>），并要求 cron_task 限群管及以上角色。
/// </summary>
public sealed class SchedulerCommandService
{
    public const string HelpText = """
        [定时与提醒 //remind]
        - //remind add <时间或5段Cron> | <提醒内容> [| <时区IANA>]：创建提醒并确认绝对触发时间
        - //remind list：查看当前会话的定时与提醒列表
        - //remind pause <jobId> / resume <jobId>：暂停或恢复提醒
        - //remind delete <jobId>：删除提醒
        """;

    private readonly IQqChatSource _source;
    private readonly SettingsBox _box;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly PluginManager _plugins;
    private readonly PluginGate _pluginGate;
    private readonly ConversationRegistry? _registry;
    private readonly IAccessRoleResolver? _roles;

    public SchedulerCommandService(
        IQqChatSource source,
        SettingsBox box,
        IPlatformPolicyResolver platformPolicies,
        PluginManager plugins,
        PluginGate pluginGate,
        ConversationRegistry? registry = null,
        IAccessRoleResolver? roles = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _platformPolicies = platformPolicies ?? throw new ArgumentNullException(nameof(platformPolicies));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _pluginGate = pluginGate ?? throw new ArgumentNullException(nameof(pluginGate));
        _registry = registry;
        _roles = roles;
    }

    public static bool IsCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = StripLeadingAtMentions(text);
        return trimmed.Equals("//remind", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//remind ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//remind\n", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//remind\r", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//remind\t", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryHandle(QqChatMessage msg)
    {
        if (!msg.AllowsCommands || !IsCommand(msg.Text)) return false;
        _ = HandleAsync(msg);
        return true;
    }

    public async Task<bool> HandleAsync(QqChatMessage msg)
    {
        if (!msg.AllowsCommands || !IsCommand(msg.Text)) return false;
        if (!_box.Current.AiModeEnabled) return true;
        var lease = PlatformSourceExecution.Current;
        var sourceKey = _source is IPlatformSourceRouter router
            && router.ConversationKey(msg, k => _registry?.Find(k) is not null) is { Length: > 0 } routedKey
            ? routedKey
            : Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId);

        var reply = ExecuteCommand(msg, sourceKey, lease);
        await SendReplyAsync(msg, lease, reply).ConfigureAwait(false);
        return true;
    }

    private string ExecuteCommand(QqChatMessage msg, string sourceKey, PlatformSourceLease? lease)
    {
        var settings = _box.Current;
        if (!settings.AiModeEnabled)
            return "机器人当前处于静默关闭状态（AI 总开关已关闭）。";

        if (ScheduledJobValidator.IsQqOfficialScope(sourceKey))
            return "QQ 官方通道受限于被动回复窗口，暂不支持无触发定时主动推送；建议使用飞书、QQ 私域或 Web 工作台。";

        if (!_pluginGate.Allows(PluginGate.SchedulerCapability)
            || !_plugins.TryGetContribution<ISchedulerContribution>("preset.feature.scheduler", out var contribution)
            || contribution is null
            || !contribution.IsReady)
        {
            return "当前未启用定时与提醒能力（preset.feature.scheduler）。";
        }

        var context = lease is not null
            ? lease.Context
            : new PlatformContext(PlatformId.Normalize(msg.Channel), AccountScope.Legacy, string.Empty);
        var platformPolicy = _platformPolicies.Resolve(context);
        var effective = ConversationProfileResolver.Resolve(settings, platformPolicy, sourceKey);
        if (!effective.FeatureEnabled("scheduler"))
            return "当前会话档案或平台策略已关闭定时与提醒能力。";
        if (!effective.AllowsReadOnlyTools)
            return "当前会话处于纯聊天模式，已禁用定时与提醒命令。";

        var raw = StripLeadingAtMentions(msg.Text);
        var body = raw.Length > 8 ? raw[8..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(body) || body.Equals("help", StringComparison.OrdinalIgnoreCase))
            return HelpText;

        if (_roles is not null)
        {
            var subject = InboundSubjectIdentity.Resolve(msg, context);
            if (_roles.TryResolveExplicitRole(subject, sourceKey, out var explicitRole) && explicitRole == WorkspaceRole.Guest)
                return "当前账号在会话中被标记为 Guest（仅聊天），已禁止使用 //remind 定时提醒命令。";
        }

        var splitIdx = body.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = (splitIdx < 0 ? body : body[..splitIdx]).ToLowerInvariant();
        var rest = splitIdx < 0 ? string.Empty : body[(splitIdx + 1)..].Trim();
        var creator = msg.Envelope?.Message.Sender.NativeUserId ?? msg.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return verb switch
        {
            "add" => HandleAdd(contribution, sourceKey, creator, rest, msg, context),
            "list" => HandleList(contribution, sourceKey),
            "pause" => HandleToggle(contribution, sourceKey, creator, rest, false, msg, context),
            "resume" => HandleToggle(contribution, sourceKey, creator, rest, true, msg, context),
            "delete" or "del" => HandleDelete(contribution, sourceKey, creator, rest, msg, context),
            _ => HelpText
        };
    }

    private string HandleAdd(ISchedulerContribution contribution, string sourceKey, string creator, string rest, QqChatMessage msg, PlatformContext context)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return "用法：//remind add <时间或5段Cron> | <提醒内容> [| <时区IANA>]";

        if (msg.IsGroup && CronSchedule.TryParse(parts[0], out _, out _) && !CanManageGroupSchedule(msg, sourceKey, context))
            return "在群聊中创建周期定时任务仅限群主/群管（GroupManager）或机器人主人（BotOwner）。";

        var tz = parts.Length >= 3 ? parts[2] : null;
        var res = contribution.CreateReminder(sourceKey, creator, parts[0], parts[1], tz);
        if (!res.Ok || res.Job is null)
        {
            return res.ReasonCode switch
            {
                "passive_trigger_required" => "QQ 官方通道受限于被动回复窗口，暂不支持无触发定时主动推送；建议使用飞书、QQ 私域或 Web 工作台。",
                "cron_interval_too_short" => "周期任务最小间隔不能低于 5 分钟。",
                "scheduler_fire_time_in_past" => "触发时间必须晚于当前时间。",
                "scheduler_scope_quota_exceeded" => $"当前会话活跃定时任务已达上限（{contribution.ListReminders(sourceKey).Length} 条）。",
                "scheduler_global_quota_exceeded" => $"全局活跃定时任务已达上限（{SchedulerLimits.MaxGlobalJobs} 条）。",
                _ => $"创建提醒失败（{res.ReasonCode}）。"
            };
        }

        CronSchedule.TryResolveTimeZone(res.Job.TimezoneIana, out var resolvedTz);
        var localFire = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(res.Job.NextFireUnix), resolvedTz);
        return $"已创建提醒 {res.Job.JobId}：将于 {localFire:yyyy-MM-dd HH:mm:ss}（{res.Job.TimezoneIana}）触发。";
    }

    private static string HandleList(ISchedulerContribution contribution, string sourceKey)
    {
        var jobs = contribution.ListReminders(sourceKey);
        if (jobs.Length == 0) return "当前会话暂无定时提醒任务。";
        var sb = new StringBuilder();
        sb.AppendLine($"当前会话定时提醒（{jobs.Length} 条）：");
        foreach (var j in jobs.Take(20))
        {
            CronSchedule.TryResolveTimeZone(j.TimezoneIana, out var tz);
            var localFire = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(j.NextFireUnix), tz);
            sb.AppendLine($"• {j.JobId}｜{j.Kind}｜{(j.Enabled ? "启用" : "暂停")}｜下次 {localFire:yyyy-MM-dd HH:mm} ({j.TimezoneIana})");
        }
        return sb.ToString().TrimEnd();
    }

    private string HandleToggle(ISchedulerContribution contribution, string sourceKey, string creator, string rest, bool enabled, QqChatMessage msg, PlatformContext context)
    {
        var jobId = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(jobId))
            return enabled ? "用法：//remind resume <jobId>" : "用法：//remind pause <jobId>";
        if (msg.IsGroup)
        {
            var existing = contribution.ListReminders(sourceKey).FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
            if (existing is null) return $"未找到提醒任务：{jobId}";
            if (!string.Equals(existing.CreatorUserId, creator, StringComparison.Ordinal) && !CanManageGroupSchedule(msg, sourceKey, context))
                return "群聊中仅创建人本人或群主/群管（GroupManager）可暂停/恢复他人的提醒任务。";
        }
        var res = contribution.SetReminderEnabled(sourceKey, jobId, enabled);
        return res.Ok
            ? $"{(enabled ? "已恢复" : "已暂停")}提醒任务：{jobId}"
            : $"未找到或无法修改提醒任务：{jobId}（{res.ReasonCode}）";
    }

    private string HandleDelete(ISchedulerContribution contribution, string sourceKey, string creator, string rest, QqChatMessage msg, PlatformContext context)
    {
        var jobId = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(jobId))
            return "用法：//remind delete <jobId>";
        if (msg.IsGroup)
        {
            var existing = contribution.ListReminders(sourceKey).FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
            if (existing is null) return $"未找到提醒任务：{jobId}";
            if (!string.Equals(existing.CreatorUserId, creator, StringComparison.Ordinal) && !CanManageGroupSchedule(msg, sourceKey, context))
                return "群聊中仅创建人本人或群主/群管（GroupManager）可删除他人的提醒任务。";
        }
        var res = contribution.DeleteReminder(sourceKey, jobId);
        return res.Ok
            ? $"已删除提醒任务：{jobId}"
            : $"未找到提醒任务：{jobId}（{res.ReasonCode}）";
    }

    private bool CanManageGroupSchedule(QqChatMessage msg, string sourceKey, PlatformContext context)
    {
        if (_roles is not null)
        {
            var subject = InboundSubjectIdentity.Resolve(msg, context);
            if (_roles.TryResolveExplicitRole(subject, sourceKey, out var explicitRole))
                return explicitRole >= WorkspaceRole.GroupManager;
            var role = _roles.ResolveRole(subject, sourceKey, _box.Current.AgentAllowedUsers, false);
            if (role >= WorkspaceRole.GroupManager) return true;
        }
        return msg.SenderRole is "owner" or "admin";
    }

    private async Task SendReplyAsync(QqChatMessage msg, PlatformSourceLease? lease, string reply)
    {
        try
        {
            if (lease is not null && !lease.IsCurrent) return;
            using var scope = PlatformSourceExecution.Enter(lease);
            using var cmdScope = Memory.MemoryUseScope.EnterCommandResponse();
            if (msg.Envelope is { } env && _source is IPlatformSourceRouter router)
            {
                var target = router.CompatibilityTarget(ConversationIdCodec.Encode(env.Message.Ref.Conversation));
                await _source.SendTextAsync(target.IsGroup, target.Id, reply).ConfigureAwait(false);
                return;
            }
            await _source.SendTextAsync(msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId, reply).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string StripLeadingAtMentions(string text)
    {
        var s = (text ?? string.Empty).Trim();
        while (s.StartsWith('@'))
        {
            var idx = s.IndexOfAny([' ', '\t', '\r', '\n']);
            if (idx < 0) return string.Empty;
            s = s[(idx + 1)..].TrimStart();
        }
        return s;
    }
}
