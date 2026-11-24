using System.Reflection;
using System.Text;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Tools;
using BotAgent.Services.Todo;
using BotAgent.Services.Tools;

namespace BotAgent.Packages.Todo;

internal sealed class PersonalTodoCoordinator(IPersonalTodoHostPorts? ports, Action<string> log) : IPersonalTodoContribution
{
    public bool IsReady => ports is not null;

    public PersonalTodoMutationResult Add(
        string sourceKey, string title, string? notes, int priority, string? dueText, string? sourceHint, string? timezoneIana)
        => ports is null
            ? new(false, false, "personal_todo_unavailable")
            : ports.Add(sourceKey, title, notes, priority, dueText, sourceHint, timezoneIana);

    public PersonalTodoItem[] List(string sourceKey, bool includeDone = false)
        => ports is null ? [] : ports.List(sourceKey, includeDone);

    public PersonalTodoMutationResult Complete(string sourceKey, string todoId)
        => ports is null
            ? new(false, false, "personal_todo_unavailable")
            : ports.Complete(sourceKey, todoId);

    public PersonalTodoMutationResult Delete(string sourceKey, string todoId)
        => ports is null
            ? new(false, false, "personal_todo_unavailable")
            : ports.Delete(sourceKey, todoId);

    internal ToolOutcome ExecuteList(string sourceKey)
    {
        if (ports is null) return ToolOutcome.Failure("personal_todo_unavailable", "");
        if (!IsPrivateCandidate(sourceKey))
            return ToolOutcome.Failure("private_conversation_only", "");

        var items = ports.List(sourceKey, includeDone: false);
        if (items.Length == 0)
            return ToolOutcome.Success("[个人待办清单]\n暂无未完成个人待办。", "todos=0");

        var sb = new StringBuilder();
        sb.AppendLine($"[个人待办清单 | 共 {items.Length} 项]");
        foreach (var item in items.Take(20))
            sb.AppendLine($"- {item.TodoId} | [P{item.Priority}] {MessageMarkers.EscapeExternalControlTags(item.Title)}");
        return ToolOutcome.Success(sb.ToString().TrimEnd(), $"todos={items.Length}");
    }

    internal ToolOutcome ExecuteAdd(string sourceKey, string title, string? notes, int priority, string? dueText)
    {
        if (ports is null) return ToolOutcome.Failure("personal_todo_unavailable", "");
        if (!IsPrivateCandidate(sourceKey))
            return ToolOutcome.Failure("private_conversation_only", "");
        var res = ports.Add(sourceKey, title, notes, priority, dueText, null, null);
        if (!res.Ok || res.Item is null)
            return ToolOutcome.Failure(res.ReasonCode, "");
        log($"[PersonalTodo] tool added {res.Item.TodoId}");
        var linkText = string.IsNullOrEmpty(res.Item.LinkedJobId) ? "" : $"（已关联提醒 {res.Item.LinkedJobId}）";
        return ToolOutcome.Success($"已添加个人待办 {res.Item.TodoId}：《{MessageMarkers.EscapeExternalControlTags(res.Item.Title)}》{linkText}", $"todoId={res.Item.TodoId}");
    }

    internal ToolOutcome ExecuteComplete(string sourceKey, string todoId)
    {
        if (ports is null) return ToolOutcome.Failure("personal_todo_unavailable", "");
        var res = ports.Complete(sourceKey, todoId);
        return res.Ok
            ? ToolOutcome.Success($"已完成个人待办：{todoId}", $"completed={todoId}")
            : ToolOutcome.Failure(res.ReasonCode, "");
    }

    internal ToolOutcome ExecuteDelete(string sourceKey, string todoId)
    {
        if (ports is null) return ToolOutcome.Failure("personal_todo_unavailable", "");
        var res = ports.Delete(sourceKey, todoId);
        return res.Ok
            ? ToolOutcome.Success($"已删除个人待办：{todoId}", $"deleted={todoId}")
            : ToolOutcome.Failure(res.ReasonCode, "");
    }

    private static bool IsPrivateCandidate(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;
        var trimmed = sourceKey.Trim();
        if (string.Equals(trimmed, "workspace:owner", StringComparison.Ordinal))
            return true;
        if (ConversationIdCodec.TryParse(trimmed, out var conv))
            return conv.Kind == ConversationKind.PrivateChat;
        return trimmed.StartsWith("private:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("feishu:private:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("local:private:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("official:private:", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class PersonalTodoTools(PersonalTodoCoordinator coordinator) : IToolContribution
{
    public ToolDefinition[] DescribeTools() =>
    [
        new(new("todo.list", ToolCategory.ConversationRead, "列出当前私聊主体的个人待办（群聊禁用）", true, "todo.actions",
            "", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.PersonalTodo, Origin: ToolOrigin.Plugin),
            []),
        new(new("todo.add", ToolCategory.ConversationRead, "新增个人待办（仅限可靠私聊/工作台）", false, "todo.actions",
            "title, notes?, priority?, due?", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.PersonalTodo, Origin: ToolOrigin.Plugin, PromptVisible: false),
            [
                new("title", Required: true, MinLength: 1, MaxLength: 200),
                new("notes", Required: false, MinLength: 0, MaxLength: 1000),
                new("priority", Required: false, MinLength: 1, MaxLength: 1),
                new("due", Required: false, MinLength: 1, MaxLength: 64)
            ]),
        new(new("todo.complete", ToolCategory.ConversationRead, "标记个人待办为已完成并取消关联提醒", false, "todo.actions",
            "todoId", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.PersonalTodo, Origin: ToolOrigin.Plugin, PromptVisible: false),
            [new("todoId", Required: true, MinLength: 3, MaxLength: 64)]),
        new(new("todo.delete", ToolCategory.ConversationRead, "删除个人待办并取消关联提醒", false, "todo.actions",
            "todoId", ToolDefaultPolicy.FollowSwitch,
            FeatureKey: ToolFeature.PersonalTodo, Origin: ToolOrigin.Plugin, PromptVisible: false),
            [new("todoId", Required: true, MinLength: 3, MaxLength: 64)])
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
            "todo.list" => coordinator.ExecuteList(sourceKey),
            "todo.add" => coordinator.ExecuteAdd(
                sourceKey,
                args["title"]!.GetValue<string>().Trim(),
                args.TryGetPropertyValue("notes", out var n) && n is not null ? n.ToString().Trim() : null,
                args.TryGetPropertyValue("priority", out var p) && p is not null && int.TryParse(p.ToString(), out var pv) ? pv : 2,
                args.TryGetPropertyValue("due", out var d) && d is not null ? d.ToString().Trim() : null),
            "todo.complete" => coordinator.ExecuteComplete(sourceKey, args["todoId"]!.GetValue<string>().Trim()),
            "todo.delete" => coordinator.ExecuteDelete(sourceKey, args["todoId"]!.GetValue<string>().Trim()),
            _ => ToolOutcome.Failure("unknown_tool", "")
        };
        return Task.FromResult(result);
    }
}

public sealed class PersonalTodoPackagePlugin : IBotPlugin, IPluginContributionSource
{
    private PersonalTodoCoordinator? _coordinator;
    private PersonalTodoTools? _tools;

    public string Id => "preset.feature.personal-todo";
    public string Name => "个人专属待办";
    public string Version => typeof(PersonalTodoPackagePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "私聊与个人工作台专属待办清单（群聊严格隔离），支持截止时间自动联动定时提醒与完成级联取消。";
    public PluginCategory Category => PluginCategory.Feature;

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ports = context.Services.GetService(typeof(IPersonalTodoHostPorts)) as IPersonalTodoHostPorts;
        var log = context.Services.GetService(typeof(Action<string>)) as Action<string> ?? (_ => { });
        _coordinator = new PersonalTodoCoordinator(ports, log);
        _tools = new PersonalTodoTools(_coordinator);
        return Task.CompletedTask;
    }

    public object? GetContribution(Type contract)
        => contract == typeof(IPersonalTodoContribution) ? _coordinator
            : contract == typeof(IToolContribution) ? _tools
            : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _coordinator = null;
        _tools = null;
        return Task.CompletedTask;
    }
}
