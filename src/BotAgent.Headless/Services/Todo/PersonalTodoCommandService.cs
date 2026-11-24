using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Todo;

/// <summary>
/// 确定性 <c>//todo</c> 个人待办命令处理器：
/// 仅在可靠私聊（<c>!msg.IsGroup &amp;&amp; msg.IsReliableIdentity</c>）生效；群聊内明确拒绝 <c>private_conversation_only</c>。
/// </summary>
public sealed class PersonalTodoCommandService
{
    public const string HelpText = """
        [个人专属待办 //todo（仅可靠私聊可用）]
        - //todo add <标题> [| <截止时间>] [| <备注>]：新增个人待办（带截止时间自动挂接提醒）
        - //todo list [all]：列出当前个人待办
        - //todo done <todoId>：标记待办完成并级联取消关联提醒
        - //todo delete <todoId>：删除待办并级联取消关联提醒
        """;

    private readonly IQqChatSource _source;
    private readonly SettingsBox _box;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly PluginManager _plugins;
    private readonly PluginGate _pluginGate;
    private readonly PersonalTodoHostPortsAdapter _ports;
    private readonly ConversationRegistry? _registry;

    public PersonalTodoCommandService(
        IQqChatSource source,
        SettingsBox box,
        IPlatformPolicyResolver platformPolicies,
        PluginManager plugins,
        PluginGate pluginGate,
        PersonalTodoHostPortsAdapter ports,
        ConversationRegistry? registry = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _platformPolicies = platformPolicies ?? throw new ArgumentNullException(nameof(platformPolicies));
        _plugins = plugins ?? throw new ArgumentNullException(nameof(plugins));
        _pluginGate = pluginGate ?? throw new ArgumentNullException(nameof(pluginGate));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _registry = registry;
    }

    public static bool IsCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = StripLeadingAtMentions(text);
        return trimmed.Equals("//todo", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//todo ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//todo\n", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//todo\r", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//todo\t", StringComparison.OrdinalIgnoreCase);
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
        _ports.ObserveSourceContext(msg, sourceKey);

        var reply = ExecuteCommand(msg, sourceKey, lease);
        await SendReplyAsync(msg, lease, reply).ConfigureAwait(false);
        return true;
    }

    private string ExecuteCommand(QqChatMessage msg, string sourceKey, PlatformSourceLease? lease)
    {
        var settings = _box.Current;
        if (!settings.AiModeEnabled)
            return "机器人当前处于静默关闭状态（AI 总开关已关闭）。";

        var isReliable = msg.Envelope?.IsReliableIdentity ?? (msg.IsReliableIdentity == true);
        if (msg.IsGroup || !isReliable)
            return "个人待办仅限可靠身份的私聊对话使用（群聊严禁读取或修改个人待办）。";

        if (!_pluginGate.Allows(PluginGate.PersonalTodoCapability)
            || !_plugins.TryGetContribution<IPersonalTodoContribution>("preset.feature.personal-todo", out var contribution)
            || contribution is null
            || !contribution.IsReady)
        {
            return "当前未启用个人待办能力（preset.feature.personal-todo）。";
        }

        var context = lease is not null
            ? lease.Context
            : new PlatformContext(PlatformId.Normalize(msg.Channel), AccountScope.Legacy, string.Empty);
        var platformPolicy = _platformPolicies.Resolve(context);
        var effective = ConversationProfileResolver.Resolve(settings, platformPolicy, sourceKey);
        if (!effective.FeatureEnabled("personal_todo"))
            return "当前会话档案或平台策略已关闭个人待办能力。";

        var raw = StripLeadingAtMentions(msg.Text);
        var body = raw.Length > 6 ? raw[6..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(body) || body.Equals("help", StringComparison.OrdinalIgnoreCase))
            return HelpText;

        var splitIdx = body.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = (splitIdx < 0 ? body : body[..splitIdx]).ToLowerInvariant();
        var rest = splitIdx < 0 ? string.Empty : body[(splitIdx + 1)..].Trim();

        return verb switch
        {
            "add" => HandleAdd(contribution, sourceKey, rest),
            "list" => HandleList(contribution, sourceKey, rest.Equals("all", StringComparison.OrdinalIgnoreCase)),
            "done" or "complete" => HandleComplete(contribution, sourceKey, rest),
            "delete" or "del" => HandleDelete(contribution, sourceKey, rest),
            _ => HelpText
        };
    }

    private static string HandleAdd(IPersonalTodoContribution contribution, string sourceKey, string rest)
    {
        var parts = rest.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
            return "用法：//todo add <标题> [| <截止时间>] [| <备注>]";
        var title = parts[0];
        var due = parts.Length >= 2 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
        var notes = parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2] : null;
        var res = contribution.Add(sourceKey, title, notes, 2, due, null, null);
        if (!res.Ok || res.Item is null)
            return $"添加待办失败（{res.ReasonCode}）。";
        var link = string.IsNullOrEmpty(res.Item.LinkedJobId) ? "" : $"（已自动联动定时提醒 {res.Item.LinkedJobId}）";
        return $"已添加个人待办 {res.Item.TodoId}：《{MessageMarkers.EscapeExternalControlTags(res.Item.Title)}》{link}";
    }

    private static string HandleList(IPersonalTodoContribution contribution, string sourceKey, bool includeDone)
    {
        var items = contribution.List(sourceKey, includeDone);
        if (items.Length == 0) return "暂无个人待办事项。";
        var sb = new StringBuilder();
        sb.AppendLine($"个人待办（{items.Length} 项）：");
        foreach (var item in items.Take(20))
            sb.AppendLine($"• {item.TodoId}｜[P{item.Priority}]｜{item.Status}｜{MessageMarkers.EscapeExternalControlTags(item.Title)}");
        return sb.ToString().TrimEnd();
    }

    private static string HandleComplete(IPersonalTodoContribution contribution, string sourceKey, string rest)
    {
        var todoId = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(todoId)) return "用法：//todo done <todoId>";
        var res = contribution.Complete(sourceKey, todoId);
        return res.Ok ? $"已完成待办并取消关联提醒：{todoId}" : $"操作失败（{res.ReasonCode}）。";
    }

    private static string HandleDelete(IPersonalTodoContribution contribution, string sourceKey, string rest)
    {
        var todoId = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(todoId)) return "用法：//todo delete <todoId>";
        var res = contribution.Delete(sourceKey, todoId);
        return res.Ok ? $"已删除待办并取消关联提醒：{todoId}" : $"操作失败（{res.ReasonCode}）。";
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
