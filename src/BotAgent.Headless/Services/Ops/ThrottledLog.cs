using System.Collections.Concurrent;

namespace BotAgent.Services.Ops;

/// <summary>
/// 节流日志：同一 key 在窗口内只输出一次（避免忙群里刷爆日志与面板）。
/// 谁要写这类日志就持一个这个，别再各自维护一份"上次什么时候写的"字典。
/// </summary>
public sealed class ThrottledLog
{
    private readonly ConcurrentDictionary<string, long> _lastAt = new();
    private readonly Action<string> _log;

    public ThrottledLog(Action<string> log) => _log = log;

    public void Write(string key, string message, int windowSeconds = 60)
    {
        var now = Clock.TickCount;
        var windowMs = Math.Max(1, windowSeconds) * 1000L;

        if (_lastAt.TryGetValue(key, out var last) && now - last < windowMs)
        {
            return;
        }

        _lastAt[key] = now;
        _log(message);
    }
}
