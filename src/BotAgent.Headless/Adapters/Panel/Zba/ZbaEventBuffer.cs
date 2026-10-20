using BotAgent.Adapters.Persistence;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

/// <summary>
/// 有界事件环：任务/配置/能力/审批的状态变化按序号追加，客户端用 after 恢复。
/// 事件只带标识、状态和错误码，不带提示词、工具载荷或聊天正文。
/// 序号先落盘、后对外确认：落盘失败的事件不发布，缺口之前的游标必须 resync。
/// 启动屏障：重启后旧游标一律先 resync 一次（无法证明上一进程没有未留证据的缺口）。
/// 首次安装必须同时满足“启动标记本次写成”与“没有任何已提交配置”：
/// 只看“标记缺失且现在可写”会把已运行过的数据目录误判成首次安装（W5-G1-BOOT-LOSS）。
/// </summary>
internal sealed class ZbaEventBuffer
{
    private const int EventCapacity = 512;
    private const int ErrorCapacity = 32;

    private readonly object _gate = new();
    private readonly Queue<ZbaEvent> _events = new();
    private readonly Queue<ZbaErrorSummary> _errors = new();
    private readonly ZbaEventSequenceStore? _sequenceStore;
    private long _sequence;
    private long? _resyncFloor;
    private long _durableGapFloor;

    internal ZbaEventBuffer(ZbaEventSequenceStore? sequenceStore = null)
        : this(sequenceStore, hadStoredConfigurationBeforeStartup: false)
    {
    }

    /// <param name="hadStoredConfigurationBeforeStartup">
    /// 本次进程初始化写入行为种子**之前**，配置库里是否已有配置
    /// （由 <see cref="BotConfig.HadStoredSettingsAtLoad" /> 在启动读取配置时捕获）。
    /// 为真说明这个数据目录运行过；与“启动标记本次写成”合起来才能排除伪首次安装。
    /// </param>
    internal ZbaEventBuffer(ZbaEventSequenceStore? sequenceStore, bool hadStoredConfigurationBeforeStartup)
    {
        _sequenceStore = sequenceStore;
        _sequence = sequenceStore?.Load() ?? 0;

        // 缺口标记必须跨重启保留：只在内存里的话，重启后离线客户端的旧游标会被误判为“已追平”。
        var gap = sequenceStore?.LoadGapFloor() ?? 0;
        if (gap > 0)
        {
            _resyncFloor = gap;
            _durableGapFloor = gap;
        }
        else if (gap < 0)
        {
            // 标记存在但读不出：不能声称连续，当前高水位也要重读状态。
            _resyncFloor = _sequence + 1;
        }

        // 启动屏障 + 游标推进（W5-G1-IO-R1/R2）：重启后无法证明上一进程没有未留证据的缺口。
        // 判据是“不能证明是首次安装”而不是“高水位 > 0”：首次事件就写失败时高水位仍是 0，
        // 但配置可能已经提交过，旧游标同样必须 resync。
        // 非首次启动时先在持久化高水位上预留一个序号（不产生事件），nextSequence 指向它；
        // 客户端按 nextSequence 追即完成，无需新事件、无需全局清除、每客户端独立。
        var firstBootClaim = sequenceStore?.TryClaimFirstBoot() ?? false;
        if (sequenceStore is not null && firstBootClaim && _sequence == 0
            && hadStoredConfigurationBeforeStartup)
        {
            // W5-G1-BOOT-LOSS：启动标记本次写成，但已有提交过的配置——上一个进程运行过，
            // 只是连标记都没写成就遇到了存储故障。高水位为 0 意味着没有任何事件序号被持久化确认过，
            // 旧游标只可能是 0（> 0 的游标会被 Read 的“超前”分支要求 resync）。
            // 因此用缺口下限让 0 重读状态即可，真实事件仍从 1 开始，不凭空预留一个从未存在过的高水位序号。
            // 缺口也写不成时进程内保持电平信号；下一次启动仍由已提交配置重新得出结论。
            var floor = Math.Max(_resyncFloor ?? 0, 1);
            _resyncFloor = floor;
            if (sequenceStore.TryMarkGap(floor)) _durableGapFloor = floor;
        }
        else if (sequenceStore is not null && (!firstBootClaim || _sequence > 0))
        {
            var barrier = Math.Max(_resyncFloor ?? 0, _sequence + 1);
            if (sequenceStore.TrySave(barrier))
            {
                _sequence = barrier;
            }

            // 预留写不成时退化为电平信号：resync 保持到下一次成功事件或成功预留。
            _resyncFloor = barrier;
        }
    }

    internal bool Append(string type, string? taskId, string? state, string? version, string? reasonCode,
        DateTimeOffset at)
    {
        lock (_gate)
        {
            var next = _sequence + 1;

            // 先落盘、后确认。把未持久化的序号发给客户端，重启后会从旧高水位继续并重用序号，
            // 旧游标就会把重用后的新事件当成“已追平”而静默跳过。
            if (_sequenceStore is not null && !_sequenceStore.TrySave(next))
            {
                // 记录最近一次缺口的序号：恢复写入后再次失败时缺口会前移，缺口之前的游标都要重读状态。
                MarkGap(next);
                return false;
            }

            // 上次没写成功的缺口标记，借成功写入的机会补写（存储此时已被证明可写）。
            if (_sequenceStore is not null && _resyncFloor is long floor && floor > _durableGapFloor)
            {
                if (_sequenceStore.TryMarkGap(floor)) _durableGapFloor = floor;
            }

            var item = new ZbaEvent(next, type, at, taskId, state, version, reasonCode);
            _sequence = next;
            _events.Enqueue(item);
            while (_events.Count > EventCapacity) _events.Dequeue();
            return true;
        }
    }

    /// <summary>
    /// 记录缺口下限并尽力落盘。落盘失败时进程内仍按缺口处理（Read 会要求 resync），
    /// 但重启后只能依赖已经写成的证据。
    /// </summary>
    private void MarkGap(long floor)
    {
        _resyncFloor = floor;
        if (_sequenceStore is null || floor <= _durableGapFloor) return;
        if (_sequenceStore.TryMarkGap(floor)) _durableGapFloor = floor;
    }

    internal void AppendError(string scope, string code, DateTimeOffset at)
    {
        lock (_gate)
        {
            _errors.Enqueue(new ZbaErrorSummary(at, scope, code));
            while (_errors.Count > ErrorCapacity) _errors.Dequeue();
        }
    }

    internal IReadOnlyList<ZbaErrorSummary> RecentErrors()
    {
        lock (_gate) return _errors.ToArray();
    }

    internal ZbaEventPage Read(long after, int limit, DateTimeOffset now)
    {
        lock (_gate)
        {
            // 落盘失败过的窗口里，缺口之前的游标错过了一段状态变化通知，必须重读状态。
            // 缺口之后（成功写入）的游标不受影响，事件照常投递。
            var resync = _resyncFloor is long floor && after < floor;

            // 空环意味着本进程尚未产生事件；若高水位已推进，after 落后就是旧事件已被重启丢弃，必须 resync。
            // 只有 after == 高水位 才是“已追平、暂无新事件”。
            if (_events.Count == 0)
                return new ZbaEventPage(Array.Empty<ZbaEvent>(), _sequence, resync || after != _sequence, now);

            resync = resync || after > _sequence;
            var oldest = _events.Peek().Sequence;
            resync = resync || after < oldest - 1;
            var items = _events
                .Where(item => item.Sequence > after)
                .Take(limit)
                .ToArray();
            var next = items.Length > 0 ? items[^1].Sequence : _sequence;
            return new ZbaEventPage(items, next, resync, now);
        }
    }
}
