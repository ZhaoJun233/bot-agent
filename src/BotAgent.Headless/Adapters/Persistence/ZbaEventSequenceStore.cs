extern alias StorageModule;

using System.Globalization;
using TextSnapshotFile = StorageModule::BotAgent.Adapters.Persistence.TextSnapshotFile;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 事件序号高水位与缺口标记：只持久化整数，确保宿主重启后序号不回退。
/// 事件内容仍在内存环；这里只解决游标恢复：高水位决定新序号，缺口下限决定旧游标是否必须 resync。
/// 两个文件共用同一套原子读写，避免再引入新的直接文件 IO。
/// </summary>
internal sealed class ZbaEventSequenceStore
{
    internal enum ReadOutcome
    {
        Ok,
        Missing,
        Failed
    }

    private readonly string _path;
    private readonly string _gapPath;
    private readonly string _bootPath;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;

    internal ZbaEventSequenceStore(string path, Func<DateTimeOffset> now, Action<string>? log)
    {
        _path = path;
        _gapPath = path + ".gap";
        _bootPath = path + ".boot";
        _now = now;
        _log = log;
    }

    internal long Load()
    {
        var outcome = TryReadText(_path, out var text, out var failure);
        if (outcome == ReadOutcome.Ok
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0)
        {
            return value;
        }

        if (outcome == ReadOutcome.Missing)
        {
            return 0;
        }

        // 损坏/读不出的序号不能静默回到 0：旧游标会在新序号追上后再次重叠。用时间代际重新起一条序号流。
        var seed = NewGenerationSeed();
        _log?.Invoke(outcome == ReadOutcome.Failed
            ? $"[Zba] 事件序号读取失败（{failure!.GetType().Name}），按新代际 {seed} 继续"
            : $"[Zba] 事件序号文件内容无效，按新代际 {seed} 继续");
        return seed;
    }

    /// <summary>
    /// 缺口下限：0 表示没有缺口标记；正数表示该序号起未证明落盘，缺口之前的游标必须 resync；
    /// 负数表示标记存在但读不出／内容无效——调用方应按“无法证明连续”保守处理。
    /// </summary>
    internal long LoadGapFloor()
    {
        var outcome = TryReadText(_gapPath, out var text, out var failure);
        if (outcome == ReadOutcome.Ok
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0)
        {
            return value;
        }

        if (outcome == ReadOutcome.Missing)
        {
            return 0;
        }

        _log?.Invoke(outcome == ReadOutcome.Failed
            ? $"[Zba] 事件缺口标记读取失败（{failure!.GetType().Name}）：按无法证明连续处理，旧游标要求 resync"
            : "[Zba] 事件缺口标记内容无效：按无法证明连续处理，旧游标要求 resync");
        return -1;
    }

    private long NewGenerationSeed()
        => _now().ToUnixTimeMilliseconds() * 1000L;

    /// <summary>
    /// 返回 false 表示序号没有落盘。调用方不得把该序号对外确认：重启后从旧高水位继续会重用序号，
    /// 旧游标会把重用后的新事件当成“已追平”而静默跳过。
    /// </summary>
    internal bool TrySave(long sequence) => WriteAtomic(_path, sequence,
        exception => $"[Zba] 事件序号写入失败（{exception.GetType().Name}）：序号未持久化，事件不对外发布，旧游标将要求 resync");

    /// <summary>
    /// 写启动标记，返回“标记是本次新建的”还是“它已经存在/写不成”。
    /// 本次新建**不等于**首次安装：上一个进程可能连标记都没写成，但配置已经提交。
    /// 调用方还必须检查既有的已提交配置证据（W5-G1-BOOT-LOSS）；标记已存在、读不出或写不成时返回 false，
    /// 按“非首次启动”保守处理。
    /// </summary>
    internal bool TryClaimFirstBoot()
    {
        var outcome = TryReadText(_bootPath, out _, out _);
        if (outcome != ReadOutcome.Missing)
        {
            return false;
        }

        return WriteAtomic(_bootPath, 1,
            exception => $"[Zba] 启动标记写入失败（{exception.GetType().Name}）：按非首次启动处理");
    }

    /// <summary>
    /// 把“该序号起未证明落盘”写成跨重启证据。返回 false 表示证据也没写成功：
    /// 进程内仍按缺口处理，但重启后无法再证明这段缺口。
    /// </summary>
    internal bool TryMarkGap(long floor) => WriteAtomic(_gapPath, floor,
        exception => $"[Zba] 事件缺口标记写入失败（{exception.GetType().Name}）：重启后可能无法证明连续");

    private bool WriteAtomic(string path, long value, Func<Exception, string> failureMessage)
        => TryWriteTextAtomic(path, value.ToString(CultureInfo.InvariantCulture), _log, failureMessage);

    /// <summary>
    /// 原子写文本：临时文件 + 覆盖移动。ZBA 的状态文件（序号/缺口/启动标记/任务回执）
    /// 共用这一处实现，面板层不直接碰文件。
    /// </summary>
    internal static bool TryWriteTextAtomic(string path, string body, Action<string>? log,
        Func<Exception, string> failureMessage)
    {
        try
        {
            // 确定性临时路径（path + ".tmp"）是既有契约：S55 用“占住这个路径”注入写失败，
            // 换成随机临时名会让跨重启缺口证据这条测试失去意义。
            TextSnapshotFile.Write(path, body, path + ".tmp");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(failureMessage(exception));
            return false;
        }
    }

    internal static ReadOutcome TryReadText(string path, out string text, out Exception? failure)
    {
        try
        {
            var snapshot = TextSnapshotFile.Read(path);
            text = snapshot?.Trim() ?? string.Empty;
            failure = null;
            return snapshot is null ? ReadOutcome.Missing : ReadOutcome.Ok;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            text = string.Empty;
            failure = exception;
            return ReadOutcome.Failed;
        }
    }
}
