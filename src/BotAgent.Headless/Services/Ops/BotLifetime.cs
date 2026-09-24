namespace BotAgent.Services.Ops;

/// <summary>
/// 进程/宿主的收摊标记（只置一次）。
///
/// 为什么单拎出来：定时器回调、戳一戳的门、兜底轮询都要问"还在干活吗"，
/// 而 <c>_disposed</c> 以前是 BotAgentHost 的私有字段 —— 每个组件都只能靠回调间接问它。
/// <see cref="MarkDisposed" /> 返回 true 表示**这一次**是第一个把它置上的（幂等收尾要用）。
/// </summary>
public sealed class BotLifetime
{
    private int _disposed;

    public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>标记为已收摊；返回 true = 这次是第一次（调用方据此决定要不要做收尾）。</summary>
    public bool MarkDisposed() => Interlocked.Exchange(ref _disposed, 1) == 0;
}
