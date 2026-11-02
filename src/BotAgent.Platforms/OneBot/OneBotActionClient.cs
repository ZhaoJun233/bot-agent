using System.Text.Json.Nodes;
using BotAgent.Services.Platforms;
using BotAgent.Services.Resilience;

namespace BotAgent.Services.OneBot;

/// <summary>
/// 动作请求的"待决响应表"：每次动作生成一个 echo，登记进表里等协议端把同 echo 的响应送回接收泵，
/// 由 <see cref="Complete" /> 兑现；超时/取消/停止都在这里收口。
///
/// 抽出来的原因：这张表和连接生命周期、入站事件分发是两件事 —— 网关只负责把回执交给这里，
/// 谁在等、等多久、停止时怎么取消，全在这个类里，不需要读一遍 1600 行网关才能确认。
/// </summary>
internal sealed class OneBotActionClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly Func<IOneBotTransport?> _transport;
    private readonly PlatformOperationLifetime _operations;
    private readonly Func<bool> _disposed;

    public OneBotActionClient(
        Func<IOneBotTransport?> transport,
        PlatformOperationLifetime operations,
        Func<bool> disposed)
    {
        _transport = transport;
        _operations = operations;
        _disposed = disposed;
    }

    /// <summary>
    /// 发一个动作并等回执（最多 10 秒）。返回 null 表示：没连接、已停止、超时、发送抛异常或取消 ——
    /// 调用方按"失败"处理即可（原来的语义就是这样，没有把不同失败拆开）。
    /// </summary>
    public async Task<JsonNode?> SendAsync(string action, string paramsJson, CancellationToken ct)
    {
        using var operation = _operations.TryEnter(ct);
        if (_disposed() || operation is null) return null;
        ct = operation.Token;
        var transport = _transport();
        if (transport is null)
        {
            return null;
        }

        var echo = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[echo] = tcs;
        }

        try
        {
            await transport.SendActionAsync(action, paramsJson, echo, ct);
            var done = await Task.WhenAny(tcs.Task, Clock.Delay(TimeSpan.FromSeconds(10), ct));
            if (done != tcs.Task)
            {
                lock (_gate)
                {
                    _pending.Remove(echo);
                }

                return null;
            }

            return await tcs.Task;
        }
        catch
        {
            lock (_gate)
            {
                _pending.Remove(echo);
            }

            return null;
        }
    }

    /// <summary>接收泵收到回执时调用：按 echo 兑现等待方；没人等（已超时/已取消）就丢掉。</summary>
    public void Complete(string echo, JsonNode root)
    {
        TaskCompletionSource<JsonNode?>? tcs;
        lock (_gate)
        {
            _pending.Remove(echo, out tcs);
        }

        tcs?.TrySetResult(root);
    }

    /// <summary>停止连接时取消所有等待方 —— 不让 HTTP 请求一直挂着。</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            _pending.Clear();
        }
    }
}
