using System.Collections.Concurrent;
using BotAgent.Services.Stickers;

namespace BotAgent.Packages.Stickers;

/// <summary>
/// Owns the full sticker operations lifecycle inside <c>preset.feature.stickers</c>:
/// inbound collection, bounded describe queue, background curation timer, album import,
/// and generation-scoped cancellation/revocation guards.
/// </summary>
public sealed class StickerCoordinator(IStickerHostPorts ports) : IStickerRuntimeContribution, IDisposable
{
    private readonly object _timerSync = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly ConcurrentQueue<string> _describeQueue = new();
    private readonly ConcurrentDictionary<string, byte> _describeQueued = new(StringComparer.Ordinal);
    private Timer? _timer;
    private Task? _activeTick;
    private int _busy;
    private int _disposed;
    private long _lastCurateAt;
    private long _describeDone;
    private int _generation;
    private int _workEnabled;

    public int PendingDescribe => _describeQueue.Count;

    public long DescribeDone => Interlocked.Read(ref _describeDone);

    public bool CanOperate
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var opts = ports.GetOptions();
            return opts.EnableStickers && opts.StickerLibraryMax > 0 && ports.IsPluginGateAllowed();
        }
    }

    public void EnsureTimer(bool enabled)
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _workEnabled, enabled ? 1 : 0);
        Timer? oldTimer;
        var created = false;
        lock (_timerSync)
        {
            oldTimer = _timer;
            _timer = null;
            if (enabled && Volatile.Read(ref _disposed) == 0)
            {
                _timer = new Timer(_ => StartTick(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                created = true;
            }
        }
        oldTimer?.Dispose();
        if (!created) return;

        foreach (var item in ports.SnapshotStore()
            .Where(s => (!s.Described || s.IsSticker is null) && s.DescribeAttempts < 2)
            .Take(30))
        {
            QueueDescribe(item.Id);
        }
    }

    private void StartTick()
    {
        var task = TickAsync();
        Volatile.Write(ref _activeTick, task);
        _ = task.ContinueWith(
            static (t, state) => Interlocked.CompareExchange(ref ((StickerCoordinator)state!)._activeTick, null, t),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void QueueDescribe(string id)
    {
        if (!string.IsNullOrEmpty(id) && _describeQueued.TryAdd(id, 0))
        {
            _describeQueue.Enqueue(id);
        }
    }

    public async Task CollectAsync(string[] urls, string fromUid, long fromGroup, long messageId,
        Func<bool>? canStartExternalIo, CancellationToken ct)
    {
        try
        {
            var added = 0;
            foreach (var url in (urls ?? Array.Empty<string>()).Take(3))
            {
                if (ct.IsCancellationRequested || !CanOperate) break;
                if (canStartExternalIo is not null && !canStartExternalIo()) break;

                var downloaded = await ports.DownloadImageAsync(url, messageId > 0 ? messageId : null, ct).ConfigureAwait(false);
                if (downloaded is not { } image) continue;
                if (!CanOperate) break;

                var record = ports.AddToStore(image.Data, image.Ext, fromUid, fromGroup);
                if (record is null) continue;

                added++;
                QueueDescribe(record.Value.Id);
            }

            if (added == 0) return;

            var max = ports.GetOptions().StickerLibraryMax;
            var evicted = ports.EnforceStoreLimit(max);
            ports.Log($"表情包库 +{added} 张（现有 {ports.StoreCount}/{max}）" +
                (evicted.Length > 0 ? $"，超限淘汰 {evicted.Length} 张" : string.Empty));
        }
        catch (Exception ex)
        {
            ports.Log($"[Sticker] 收集表情包失败：{ex.Message}");
        }
    }

    public async Task<string> CurateAsync(bool force, CancellationToken ct = default)
    {
        if (!CanOperate)
        {
            return "表情包功能已停用（插件或功能开关），未执行巡检";
        }

        var all = ports.SnapshotStore();
        if (all.Length == 0) return "库里还没有表情包";
        if (!force && all.Length < 6) return $"只有 {all.Length} 张，先不删";

        var now = ports.UtcNow.ToUnixTimeSeconds();
        var table = string.Join("\n", all.Take(200).Select(s =>
            $"{s.Id} | {DescribeItem(s)} | 用过 {s.Uses} 次 | {(now - s.AddedAt) / 3600} 小时前收藏" +
            (s.LastUsedAt > 0 && now - s.LastUsedAt < 86400 ? " | 24h内用过" : string.Empty)));

        var maxDelete = Math.Max(2, all.Length / 5);
        if (!CanOperate) return "表情包功能已停用（插件或功能开关），未执行巡检";

        ct.ThrowIfCancellationRequested();
        var decision = await ports.CurateStickersAsync(table, maxDelete, ct).ConfigureAwait(false);
        if (!CanOperate) return "表情包功能已停用，巡检结果未应用";

        if (decision.WantedIds is not { Length: > 0 })
        {
            ports.Log($"表情包巡检：不删（{all.Length} 张）" +
                (string.IsNullOrWhiteSpace(decision.Reason) ? string.Empty : $"——{decision.Reason}"));
            return "本次没有需要删除的";
        }

        var deleted = 0;
        foreach (var id in decision.WantedIds)
        {
            var item = ports.FindInStore(id);
            if (item is null) continue;
            if (item.Value.LastUsedAt > 0 && now - item.Value.LastUsedAt < 86400) continue;
            if (ports.RemoveFromStore(id, "自巡检")) deleted++;
        }

        var max = ports.GetOptions().StickerLibraryMax;
        var summary = $"表情包巡检：删除 {deleted} 张（{decision.Reason ?? "模型未给理由"}）";
        ports.Log(summary + $"，现有 {ports.StoreCount}/{max} 张");
        ports.EnforceStoreLimit(max);
        return summary;
    }

    public async Task<string> ImportFromAlbumAsync(int limit = 30, CancellationToken ct = default)
    {
        if (!CanOperate)
        {
            return "表情包功能已停用（插件或功能开关），未执行导入";
        }

        ct.ThrowIfCancellationRequested();
        var urls = await ports.FetchCustomFacesAsync(Math.Clamp(limit, 1, 200), ct).ConfigureAwait(false);
        if (urls.Length == 0)
        {
            return "协议端没有返回收藏表情（可能未登录，或协议端不支持 fetch_custom_face）";
        }

        var added = 0;
        var revoked = false;
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            if (!CanOperate) { revoked = true; break; }

            var downloaded = await ports.DownloadImageAsync(url, null, ct).ConfigureAwait(false);
            if (downloaded is not { } image) continue;

            ct.ThrowIfCancellationRequested();
            if (!CanOperate) { revoked = true; break; }

            var record = ports.AddToStore(image.Data, image.Ext, null, 0);
            if (record is null) continue;

            added++;
            QueueDescribe(record.Value.Id);
        }

        var max = ports.GetOptions().StickerLibraryMax;
        var evicted = ports.EnforceStoreLimit(max);
        var summary = $"从 QQ 收藏表情导入 {added} 张（收到 {urls.Length} 个地址，现有 {ports.StoreCount}/{max}）" +
                      (evicted.Length > 0 ? $"，超限淘汰 {evicted.Length} 张" : string.Empty) +
                      (revoked ? "（功能已停用，提前结束）" : string.Empty);
        ports.Log(summary);
        return summary;
    }

    private bool WorkAllowed(int generation)
        => Volatile.Read(ref _disposed) == 0
           && Volatile.Read(ref _workEnabled) == 1
           && Volatile.Read(ref _generation) == generation
           && !_shutdownCts.IsCancellationRequested;

    private void DisarmTimer()
    {
        Timer? old;
        lock (_timerSync)
        {
            old = _timer;
            _timer = null;
        }
        old?.Dispose();
    }

    private async Task TickAsync()
    {
        var generation = Volatile.Read(ref _generation);
        if (!WorkAllowed(generation) || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            if (!CanOperate)
            {
                DisarmTimer();
                return;
            }
            var ct = _shutdownCts.Token;
            await DrainDescribeAsync(generation, ct).ConfigureAwait(false);
            if (!WorkAllowed(generation) || !CanOperate) return;

            var interval = Math.Max(0, ports.GetOptions().StickerCurateIntervalSeconds);
            var now = ports.UtcNow.ToUnixTimeSeconds();
            if (interval > 0 && now - Volatile.Read(ref _lastCurateAt) >= interval)
            {
                Volatile.Write(ref _lastCurateAt, now);
                await CurateAsync(force: false, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ports.Log($"[Sticker] 表情包巡检异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task DrainDescribeAsync(int generation, CancellationToken ct)
    {
        while (WorkAllowed(generation) && CanOperate && !ct.IsCancellationRequested)
        {
            if (!_describeQueue.TryDequeue(out var id)) return;
            _describeQueued.TryRemove(id, out _);

            var item = ports.FindInStore(id);
            if (item is null || item.Value.DescribeAttempts >= 2) continue;
            if (item.Value.Described && item.Value.IsSticker is not null) continue;
            if (!WorkAllowed(generation) || !CanOperate) { QueueDescribe(id); return; }

            byte[] bytes;
            try { bytes = await ports.ReadStickerBytesAsync(id).ConfigureAwait(false); }
            catch { if (WorkAllowed(generation) && CanOperate) ports.MarkDescribeFailed(id); continue; }

            if (!WorkAllowed(generation) || !CanOperate) { QueueDescribe(id); return; }

            var mime = item.Value.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };

            var res = await ports.DescribeStickerAsync(bytes, mime, ct).ConfigureAwait(false);
            if (!WorkAllowed(generation) || !CanOperate)
            {
                QueueDescribe(id);
                return;
            }
            if (res.Description is null && res.Tags is null && res.IsSticker is null)
            {
                ports.MarkDescribeFailed(id);
                continue;
            }

            if (res.IsSticker == false)
            {
                ports.RemoveFromStore(id, "审核：不像表情包（截图/广告/纯文字图）");
                ports.Log($"表情包审核不通过，已丢弃 #{id}：{res.Description ?? "(无描述)"}");
                continue;
            }

            ports.SetDescription(id, res.Description, res.Tags, res.IsSticker);
            Interlocked.Increment(ref _describeDone);
            var updated = ports.FindInStore(id) ?? item.Value;
            ports.Log($"表情包入库：#{id} {DescribeItem(updated)}");
        }
    }

    private static string DescribeItem(StickerItemSnapshot s)
    {
        var tags = s.Tags is { Length: > 0 } ? string.Join("/", s.Tags) : string.Empty;
        if (!string.IsNullOrWhiteSpace(s.Description) && tags.Length > 0) return $"{s.Description}（{tags}）";
        if (!string.IsNullOrWhiteSpace(s.Description)) return s.Description;
        if (tags.Length > 0) return tags;
        return "(待描述)";
    }

    internal async Task ShutdownAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _workEnabled, 0);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisarmTimer();
            try { _shutdownCts.Cancel(); } catch { }
        }
        if (Volatile.Read(ref _activeTick) is { } active)
        {
            try { await active.WaitAsync(ct).ConfigureAwait(false); } catch { }
        }
        _shutdownCts.Dispose();
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _workEnabled, 0);
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DisarmTimer();
        try { _shutdownCts.Cancel(); } catch { }
        _shutdownCts.Dispose();
    }
}
