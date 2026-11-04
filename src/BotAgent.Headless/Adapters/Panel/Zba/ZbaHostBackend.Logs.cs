using BotAgent.Domain.Configuration;
using BotAgent.Panel;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel.Zba;

internal sealed partial class ZbaHostBackend
{
    /// <summary>
    /// 只读运行日志的安全摘要：把 <see cref="FileLog"/> 的环形缓冲交给
    /// <see cref="RuntimeLogProjection"/> 做固定分类投影，原文不出宿主进程。
    /// </summary>
    public ValueTask<ZbaLogPage> ListLogsAsync(
        ConfigurationCaller caller, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var bounded = Math.Clamp(limit, 1, ZbaContract.MaxLogEntries);
        return ValueTask.FromResult(RuntimeLogProjection.Project(FileLog.RecentRuntimeLines(bounded), bounded));
    }
}
