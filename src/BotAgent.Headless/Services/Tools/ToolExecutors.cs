using System;
using System.Text.Json.Nodes;
using BotAgent.Domain.Tools;

namespace BotAgent.Services.Tools;

/// <summary>
/// 执行者的**登记**契约（general-agent-platform-plan.md §5.1）。
///
/// 只回答三件事：叫什么（<see cref="Id" />，与 <see cref="ToolSpec.Executor" /> 对应）、
/// 今天是谁在干（<see cref="Implementation" />）、以及**是不是还走老路径**
/// （<see cref="LegacyPath" /> = true 表示登记了但还没接进统一执行）。
///
/// 为什么把“登记”与“能执行”（<see cref="IToolExecutor" />）拆成两层：这个仓库里四种执行形态并存，
/// 搬执行是 §10.2 坑 3 明令要谨慎的事 —— 类型上分得开，探针才能断言
/// “哪些是真实现、哪些只是占位”，而不是靠读注释。
/// </summary>
public interface IToolExecutorRegistration
{
    /// <summary>执行者标识（<see cref="ToolSpec.Executor" /> 指向它；一个执行者可以接多个工具）。</summary>
    string Id { get; }

    /// <summary>今天真正干这件事的组件（面板与审计展示用；不含参数与正文）。</summary>
    string Implementation { get; }

    /// <summary>true = 仍由**各自的老路径**执行（聊天的 if/else），还没接进统一执行。</summary>
    bool LegacyPath { get; }
}

/// <summary>
/// **能真执行**的那种执行者（`//` 那两族就是）：调用方给一次 <see cref="ToolCall" />，
/// 它负责过完自己的内部检查后执行，并回一份 <see cref="ToolOutcome" />。
///
/// 它**不做判定**：白名单/闸门/预算都在调用方（<c>ToolGate</c> 与各自的用例）——
/// 执行者只该管“怎么干”，不该管“能不能干”（§3.1 的铁律）。
/// </summary>
public interface IToolExecutor : IToolExecutorRegistration
{
    /// <summary>执行一次。<paramref name="ct" /> 取消时抛 <see cref="OperationCanceledException" />（不吞）。</summary>
    Task<ToolOutcome> ExecuteAsync(ToolCall call, CancellationToken ct = default);
}

/// <summary>执行者登记表（Id → 执行者）。装配点建一份；面板与探针只读它。</summary>
public sealed class ToolExecutorRegistry
{
    private readonly Dictionary<string, IToolExecutorRegistration> _byId = new(StringComparer.Ordinal);

    public static ToolExecutorRegistry Empty { get; } = new();

    public ToolExecutorRegistry Register(IToolExecutorRegistration executor)
    {
        _byId[executor.Id] = executor;
        return this;
    }

    public bool TryGet(string? id, out IToolExecutorRegistration executor)
        => _byId.TryGetValue(id ?? string.Empty, out executor!);

    public IReadOnlyCollection<IToolExecutorRegistration> All => _byId.Values;

    public int Count => _byId.Count;
}

/// <summary>只登记、不执行的那几种（聊天那两族）：把“还没接进统一执行”如实说出来。</summary>
internal sealed record DeclaredExecutor(string Id, string Implementation, bool LegacyPath) : IToolExecutorRegistration;

/// <summary>内建执行者登记：`//` 两族是真实现，聊天两族是占位（LegacyPath）。</summary>
public static class BuiltinToolExecutors
{
    public static ToolExecutorRegistry Create() => new ToolExecutorRegistry()
        .Register(new DeclaredExecutor(
            ChatToolSpecs.ExecutorActions,
            "Services/Reply/ReplyPipeline.cs（动作闸门与执行）",
            LegacyPath: true))
        .Register(new DeclaredExecutor(
            ChatToolSpecs.ExecutorApproval,
            "Services/Permissions/ApprovalUseCase.cs（审批与提问）",
            LegacyPath: true))
        // 这两个在运行时由真实对象实现 IToolExecutor（见 ServerAgentRunner / SessionQqActionHost）；
        // 这里登记的是“谁在执行”，用于目录自检与面板展示。
        .Register(new DeclaredExecutor(
            QqToolSpecs.ExecutorActions,
            "Services/Agent/QqActionTool.cs（SessionQqActionHost，实现 IToolExecutor）",
            LegacyPath: false))
        .Register(new DeclaredExecutor(
            ServerToolSpecs.ExecutorAgent,
            "Services/Agent/ServerAgentRunner.cs（实现 IToolExecutor）",
            LegacyPath: false));
}
