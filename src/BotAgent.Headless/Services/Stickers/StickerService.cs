using System.Collections.Concurrent;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Agent;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Stickers;

/// <summary>
/// 表情包运营（批次 4 从 <c>BotAgentHost</c> 抽出的用例类）：收图 → 排队让模型写说明/关键词 →
/// 自己去巡检删掉不合适的 → 按频率门决定要不要发。
///
/// 边界：**只管表情包这一件事**。它不判断"这一轮该不该说话"（那在回复流程里），
/// 也不决定"这条回复带不带表情包"（那是决策结果里的 stickerId）。
/// 依赖全部由构造函数注入（装配见 <c>Host/CompositionRoot</c>）—— 不再自己 new 具体实现。
/// </summary>
public sealed class StickerService : IDisposable
{
    private readonly IStickerRepository _store;
    private readonly IModelClient _brain;
    private readonly IQqChatSource _source;
    private readonly SettingsBox _box;
    private readonly Action<string> _log;

    private readonly ConcurrentQueue<string> _describeQueue = new();
    private readonly ConcurrentDictionary<string, byte> _describeQueued = new();

    /// <summary>每个会话最近一次发表情包（频率门用）。</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Id)> _lastSent = new();

    private Timer? _timer;
    private int _busy;
    private int _disposed;
    private long _lastCurateAt;
    private long _describeDone;

    private AppSettings _settings => _box.Current;

    public StickerService(IStickerRepository store, IModelClient brain, IQqChatSource source, SettingsBox box, Action<string> log)
    {
        _store = store;
        _brain = brain;
        _source = source;
        _box = box;
        _log = log;
    }

    /// <summary>表情包库（面板展示、回复流程挑候选都用它）。</summary>
    public IStickerRepository Store => _store;

    /// <summary>正在等待生成说明的张数。</summary>
    public int PendingDescribe => _describeQueue.Count;

    /// <summary>已生成说明的张数（含历史累计）。</summary>
    public long DescribeDone => Interlocked.Read(ref _describeDone);

    /// <summary>首次用到时把库读进来（库索引在 SQLite，图片本体在 stickers/）。</summary>
    public void Load(string runtimeRoot) => _store.Load(runtimeRoot);

    /// <summary>
    /// 按当前配置重建巡检定时器（只在开关/上限真的变了时才重建 —— 否则每保存一次设置就把计时归零，
    /// 间隔一到就永远等不到下一轮）。用例侧的"配置变了"信号统一走这里。
    /// </summary>
    public void EnsureTimer(bool enabled)
    {
        _timer?.Dispose();
        _timer = null;
        if (!enabled || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // 10 秒一小步：刚收的图很快就能补上说明（不然检索不到它）
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        // 启动后稍等一会儿先补一轮：重启后积压的未识别图 + 审核功能上线前入库的旧图
        foreach (var item in _store.Snapshot().Where(s => (!s.Described || s.IsSticker is null) && s.DescribeAttempts < 2).Take(30))
        {
            QueueDescribe(item.Id);
        }
    }

    /// <summary>
    /// 把群友发的图收进表情包库（同一张内容只存一份），并排队让模型生成说明/关键词。
    /// 完全不阻塞接收线程（下载、写盘、模型调用都在这里）。
    /// </summary>
    public async Task CollectAsync(List<string> urls, string fromUid, long fromGroup, long messageId = 0)
    {
        try
        {
            var added = 0;
            foreach (var url in urls.Take(3))
            {
                if (_settings.StickerLibraryMax <= 0)
                {
                    break;
                }

                var downloaded = await _brain.DownloadImageAsync(url, CancellationToken.None, messageId > 0 ? messageId : null);
                if (downloaded is not { } image)
                {
                    continue;
                }

                var record = _store.Add(image.Data, image.Ext, fromUid, fromGroup);
                if (record is null)
                {
                    continue; // 重复图
                }

                added++;
                QueueDescribe(record.Id);
            }

            if (added == 0)
            {
                return;
            }

            var evicted = _store.EnforceLimit(_settings.StickerLibraryMax);
            _log($"表情包库 +{added} 张（现有 {_store.Count}/{_settings.StickerLibraryMax}）" +
                 (evicted.Count > 0 ? $"，超限淘汰 {evicted.Count} 张" : string.Empty));
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"收集表情包失败：{ex.Message}");
        }
    }

    /// <summary>排队等模型写说明（同一张只排一次）。</summary>
    public void QueueDescribe(string id)
    {
        if (_describeQueued.TryAdd(id, 0))
        {
            _describeQueue.Enqueue(id);
        }
    }

    /// <summary>巡检一步：先补说明，再看要不要自巡检（自巡检由自己的定时器驱动）。</summary>
    private async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            await DrainDescribeAsync();

            var interval = Math.Max(0, _settings.StickerCurateIntervalSeconds);
            var now = Clock.UtcNow.ToUnixTimeSeconds();
            if (interval > 0 && now - Volatile.Read(ref _lastCurateAt) >= interval)
            {
                Volatile.Write(ref _lastCurateAt, now);
                await CurateAsync(force: false);
            }
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包巡检异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>把排队里的图交给模型写说明/关键词；审核结果决定入库还是丢掉。</summary>
    private async Task DrainDescribeAsync()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            if (!_describeQueue.TryDequeue(out var id))
            {
                return;
            }

            _describeQueued.TryRemove(id, out _);

            var item = _store.Find(id);
            if (item is null || item.DescribeAttempts >= 2)
            {
                continue;
            }

            // 已描述过但还没审核过的（审核功能上线前入库的旧图）也要补审一次
            if (item.Described && item.IsSticker is not null)
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await _store.ReadBytesAsync(item);
            }
            catch
            {
                _store.MarkDescribeFailed(id);
                continue;
            }

            var mime = item.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };

            var (desc, tags, isSticker) = await _brain.DescribeStickerAsync(bytes, mime);
            if (desc is null && tags is null && isSticker is null)
            {
                _store.MarkDescribeFailed(id);
                continue;
            }

            if (isSticker == false)
            {
                // 审核不通过（聊天截图 / 广告 / 纯文字图…）→ 立即丢掉。
                // 线上实测把群友发的**聊天截图**当表情包收了、还准备发出去 —— 这一步就是闸门。
                _store.Remove(id, "审核：不像表情包（截图/广告/纯文字图）");
                _log($"表情包审核不通过，已丢弃 #{id}：{desc ?? "(无描述)"}");
                continue;
            }

            _store.SetDescription(id, desc, tags, isSticker);
            Interlocked.Increment(ref _describeDone);
            _log($"表情包入库：#{id} {StickerText.Describe(_store.Find(id) ?? item)}");
        }
    }

    /// <summary>
    /// 让机器人自己巡检表情包库，决定删哪些（用户要求：它应自己删/加）。
    /// 保护规则：24 小时内用过的代码侧直接拦下；一次最多删库里 1/5（至少能给模型 2 个名额）。
    /// </summary>
    public async Task<string> CurateAsync(bool force)
    {
        var all = _store.Snapshot();
        if (all.Count == 0)
        {
            return "库里还没有表情包";
        }

        if (!force && all.Count < 6)
        {
            return $"只有 {all.Count} 张，先不删";
        }

        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var table = string.Join("\n", all.Take(200).Select(s =>
            $"{s.Id} | {StickerText.Describe(s)} | 用过 {s.Uses} 次 | {(now - s.AddedAt) / 3600} 小时前收藏" +
            (s.LastUsedAt > 0 && now - s.LastUsedAt < 86400 ? " | 24h内用过" : string.Empty)));

        var maxDelete = Math.Max(2, all.Count / 5);
        var (wanted, reason) = await _brain.CurateStickersAsync(table, maxDelete);
        if (wanted.Count == 0)
        {
            _log($"表情包巡检：不删（{all.Count} 张）" + (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"——{reason}"));
            return "本次没有需要删除的";
        }

        var deleted = 0;
        foreach (var id in wanted)
        {
            var item = _store.Find(id);
            if (item is null)
            {
                continue;
            }

            // 代码兜底：刚用过的别删（模型有时看不见标注）
            if (item.LastUsedAt > 0 && now - item.LastUsedAt < 86400)
            {
                continue;
            }

            if (_store.Remove(id, "自巡检"))
            {
                deleted++;
            }
        }

        var summary = $"表情包巡检：删除 {deleted} 张（{reason ?? "模型未给理由"}）";
        _log(summary + $"，现有 {_store.Count}/{_settings.StickerLibraryMax} 张");

        // 巡检完顺手把容量压回上限（例如用户把上限改小了）
        _store.EnforceLimit(_settings.StickerLibraryMax);
        return summary;
    }

    /// <summary>
    /// 从登录账号在 QQ 里的“收藏表情”导入一批（机器人自己“添加”表情包的来源）。
    /// 拉图/存图失败都只是跳过，不影响其它功能。
    /// </summary>
    public async Task<string> ImportFromAlbumAsync(int limit = 30)
    {
        if (_settings.StickerLibraryMax <= 0)
        {
            return "表情包库上限为 0，先在设置里放开";
        }

        var urls = await _source.FetchCustomFacesAsync(Math.Clamp(limit, 1, 200));
        if (urls.Count == 0)
        {
            return "协议端没有返回收藏表情（可能未登录，或协议端不支持 fetch_custom_face）";
        }

        var added = 0;
        foreach (var url in urls)
        {
            var downloaded = await _brain.DownloadImageAsync(url, CancellationToken.None);
            if (downloaded is not { } image)
            {
                continue;
            }

            var record = _store.Add(image.Data, image.Ext, null, 0);
            if (record is null)
            {
                continue;
            }

            added++;
            QueueDescribe(record.Id);
        }

        var evicted = _store.EnforceLimit(_settings.StickerLibraryMax);
        var summary = $"从 QQ 收藏表情导入 {added} 张（收到 {urls.Count} 个地址，现有 {_store.Count}/{_settings.StickerLibraryMax}）" +
                      (evicted.Count > 0 ? $"，超限淘汰 {evicted.Count} 张" : string.Empty);
        _log(summary);
        return summary;
    }

    /// <summary>
    /// 表情包频率门。为什么要它：
    /// 线上实测库里只有两张时，模型会**每句都挂同一张**，群友直接开愤
    /// （“你别老是发这个表情包了”“这个bot只发奶龙”）。表情包是调味品，不是主食。
    /// </summary>
    public bool Allow(string sourceKey, string stickerId, out string reason)
    {
        reason = string.Empty;
        var cooldown = Math.Max(0, _settings.StickerCooldownSeconds);
        if (!_lastSent.TryGetValue(sourceKey, out var last))
        {
            return true;
        }

        var since = Clock.Now - last.At;
        if (cooldown > 0 && since.TotalSeconds < cooldown)
        {
            reason = $"距上次发表情包只有 {since.TotalSeconds:F0} 秒（冷却 {cooldown} 秒）";
            return false;
        }

        // 同一张别在同一个会话里反复用（库里就那几张时最容易退化成“只会发这一张”）
        if (string.Equals(last.Id, stickerId, StringComparison.OrdinalIgnoreCase) && since.TotalMinutes < 10)
        {
            reason = $"这张 {stickerId} 十分钟内已经发过";
            return false;
        }

        return true;
    }

    /// <summary>发送一张表情包（读文件 → base64 → 协议端 image 段）。失败只记日志，不影响文字回复。
    /// 发出去才记账 —— 这条日志也是排查“为什么又发了”的唯一现场。</summary>
    public async Task<bool> SendAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo, string sourceKey)
    {
        try
        {
            var bytes = await _store.ReadBytesAsync(sticker);
            var ok = await _source.SendImageAsync(isGroup, targetId, bytes, replyToMessageId: replyTo);
            if (ok)
            {
                var sinceText = _lastSent.TryGetValue(sourceKey, out var prev)
                    ? $"（距上次发表情包 {(Clock.Now - prev.At).TotalSeconds:F0} 秒）"
                    : string.Empty;
                _lastSent[sourceKey] = (Clock.Now, sticker.Id);
                _log($"已发表情包 #{sticker.Id}{sinceText}");
            }

            return ok;
        }
        catch (Exception ex)
        {
            _log($"表情包发送失败（#{sticker.Id}）：{ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _timer?.Dispose();
        _timer = null;
    }
}
