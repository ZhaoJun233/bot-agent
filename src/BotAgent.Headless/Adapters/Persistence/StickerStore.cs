using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Stickers;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 表情包库：**全局共用一份**（不分会话）—— 用户明确要求“不同会话公用一个表情包存储就可以了”。
///
/// 存储：
///   data/qqchat.db 里的 stickers 表   索引（说明、关键词、使用次数、是否表情包）
///   data/stickers/&lt;hash&gt;.&lt;ext&gt;      图片本体
/// 为什么图片不放进库：二进制大对象放库里之后，备份/预览/清理都会变麻烦（BLOB 不能直接给面板当图片返回），
/// 索引进库已经拿到“一句 SQL 查库/去重/统计”的好处了。
///
/// 线程模型：内部锁 + 立即落库（库很小，几十~几千条，写入不频繁）。
/// 容量：超过 StickerLibraryMax 时按“用得少 + 最久没用”淘汰 —— 见 <see cref="EnforceLimit"/>。
/// </summary>
public sealed class StickerStore : IStickerRepository
{
    private readonly object _gate = new();
    private readonly List<StickerRecord> _items = new();
    private string _dir = string.Empty;

    /// <summary>库里现在有多少张。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>已生成说明的张数（没说明的没法按语境检索，只能随机兜底）。</summary>
    public int DescribedCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(i => i.Described);
            }
        }
    }

    public void Load(string dataRoot)
    {
        _dir = Path.Combine(dataRoot, "stickers");
        Directory.CreateDirectory(_dir);

        lock (_gate)
        {
            _items.Clear();
            try
            {
                // 索引现在在 SQLite 里（老版本是 stickers/index.json，由 LegacyJsonImporter 导入）。
                // 图片本体仍然放 stickers/ 目录 —— 二进制大对象不适合塞库（备份/预览/清理都不方便）。
                var loaded = AppDatabase.Query("""
                    SELECT id, hash, file, ext, bytes, added_unix, last_used_unix, uses, from_uid, from_group,
                           description, tags, is_sticker, described, describe_attempts
                    FROM stickers
                    """, r => new StickerRecord
                {
                    Id = AppDatabase.Str(r, "id") ?? string.Empty,
                    Hash = AppDatabase.Str(r, "hash") ?? string.Empty,
                    File = AppDatabase.Str(r, "file") ?? string.Empty,
                    Ext = AppDatabase.Str(r, "ext") ?? "png",
                    Bytes = AppDatabase.Long(r, "bytes"),
                    AddedAt = AppDatabase.Long(r, "added_unix"),
                    LastUsedAt = AppDatabase.Long(r, "last_used_unix"),
                    Uses = AppDatabase.Int(r, "uses"),
                    FromUid = AppDatabase.Str(r, "from_uid"),
                    FromGroup = AppDatabase.Long(r, "from_group"),
                    Desc = AppDatabase.Str(r, "description"),
                    Tags = ParseTags(AppDatabase.Str(r, "tags")),
                    IsSticker = AppDatabase.LongOrNull(r, "is_sticker") is long v ? v != 0 : null,
                    Described = AppDatabase.Bool(r, "described"),
                    DescribeAttempts = AppDatabase.Int(r, "describe_attempts")
                });

                foreach (var item in loaded)
                {
                    item.AbsolutePath = Path.Combine(_dir, item.File);
                    if (File.Exists(item.AbsolutePath))
                    {
                        _items.Add(item);
                    }
                }

                FileLog.Write("Sticker", $"已恢复 {_items.Count} 张表情包（其中 {DescribedCount} 张有说明）");
            }
            catch (Exception ex)
            {
                FileLog.Warn("Sticker", $"表情包索引读取失败：{ex.Message}");
            }
        }
    }

    /// <summary>标签列是 JSON 数组（库里存字符串，方便以后用 json_each 查）。</summary>
    private static List<string> ParseTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }

    /// <summary>快照（面板展示 / 提示词候选都用它，避免锁外遍历可变集合）。</summary>
    public List<StickerRecord> Snapshot()
    {
        lock (_gate)
        {
            return _items.Select(Clone).ToList();
        }
    }

    public StickerRecord? Find(string id)
    {
        lock (_gate)
        {
            var hit = _items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            return hit is null ? null : Clone(hit);
        }
    }

    /// <summary>图片本体还在不在盘上（面板按 id 取图前要判一下，别把 404 变成异常）。</summary>
    public static bool ExistsOnDisk(StickerRecord item)
        => !string.IsNullOrEmpty(item.AbsolutePath) && File.Exists(item.AbsolutePath);

    /// <summary>读图片字节（发送 / 面板取图都走它）。文件不在时抛 —— 由调用方决定怎么降级。</summary>
    public Task<byte[]> ReadBytesAsync(StickerRecord item, CancellationToken ct = default)
        => File.ReadAllBytesAsync(item.AbsolutePath, ct);

    /// <summary>
    /// 收藏一张图。同一张内容已存在时直接返回 null（不重复存、不重复描述）。
    /// </summary>
    public StickerRecord? Add(byte[] data, string ext, string? fromUid, long fromGroup)
    {
        if (data.Length == 0)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var id = hash[..8];
        ext = NormalizeExt(ext);

        lock (_gate)
        {
            if (_items.Any(i => i.Hash == hash))
            {
                return null; // 已经收藏过
            }

            var fileName = $"{id}-{hash[8..16]}.{ext}";
            var full = Path.Combine(_dir, fileName);
            try
            {
                File.WriteAllBytes(full, data);
            }
            catch (Exception ex)
            {
                FileLog.Warn("Sticker", $"表情包写入失败：{ex.Message}");
                return null;
            }

            var record = new StickerRecord
            {
                Id = id,
                Hash = hash,
                File = fileName,
                AbsolutePath = full,
                Ext = ext,
                Bytes = data.Length,
                AddedAt = Clock.UtcNow.ToUnixTimeSeconds(),
                FromUid = fromUid,
                FromGroup = fromGroup
            };

            _items.Add(record);
            SaveLocked();
            return Clone(record);
        }
    }

    /// <summary>记一次使用（“用得多”的图在淘汰时更安全）。</summary>
    public void MarkUsed(string id)
    {
        lock (_gate)
        {
            var hit = _items.FirstOrDefault(i => i.Id == id);
            if (hit is null)
            {
                return;
            }

            hit.Uses++;
            hit.LastUsedAt = Clock.UtcNow.ToUnixTimeSeconds();
            SaveLocked();
        }
    }

    /// <summary>模型给出的说明/关键词/是否表情包判定落库。</summary>
    public void SetDescription(string id, string? desc, IEnumerable<string>? tags, bool? isSticker = null)
    {
        lock (_gate)
        {
            var hit = _items.FirstOrDefault(i => i.Id == id);
            if (hit is null)
            {
                return;
            }

            hit.Desc = string.IsNullOrWhiteSpace(desc) ? hit.Desc : desc!.Trim();
            if (tags is not null)
            {
                var list = tags
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct()
                    .Take(8)
                    .ToList();
                if (list.Count > 0)
                {
                    hit.Tags = list;
                }
            }

            if (isSticker is not null)
            {
                hit.IsSticker = isSticker;
            }

            hit.Described = !string.IsNullOrWhiteSpace(hit.Desc) || hit.Tags.Count > 0;
            SaveLocked();
        }
    }

    /// <summary>记一次描述失败（用于放弃重试，避免对一张烂图永远重试）。</summary>
    public void MarkDescribeFailed(string id)
    {
        lock (_gate)
        {
            var hit = _items.FirstOrDefault(i => i.Id == id);
            if (hit is null)
            {
                return;
            }

            hit.DescribeAttempts++;
            SaveLocked();
        }
    }

    /// <summary>删除一张（机器人自决定或面板手动都会走这里）。</summary>
    public bool Remove(string id, string? reason = null)
    {
        lock (_gate)
        {
            var hit = _items.FirstOrDefault(i => i.Id == id);
            if (hit is null)
            {
                return false;
            }

            _items.Remove(hit);
            TryDeleteFile(hit.AbsolutePath);
            SaveLocked();
            FileLog.Write("Sticker", $"已删除表情包 {id}" +
                                     (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"（{reason}）") +
                                     $"：{StickerText.Describe(hit)}");
            return true;
        }
    }

    /// <summary>
    /// 容量上限：超出就淘汰。评分 = 使用次数 ×3 − 闲置天数，最低的先删。
    /// 这样“常用的留着、从没用过的先走”，且不会因为刚收藏就被删（新图闲置天数为 0）。
    /// </summary>
    public List<StickerRecord> EnforceLimit(int max)
    {
        if (max <= 0)
        {
            return new List<StickerRecord>();
        }

        List<StickerRecord> removed = new();
        lock (_gate)
        {
            if (_items.Count <= max)
            {
                return removed;
            }

            var now = Clock.UtcNow.ToUnixTimeSeconds();
            var victims = _items
                .OrderBy(i => Score(i, now))
                .ThenBy(i => i.AddedAt)
                .Take(_items.Count - max)
                .ToList();

            foreach (var victim in victims)
            {
                _items.Remove(victim);
                TryDeleteFile(victim.AbsolutePath);
                removed.Add(Clone(victim));
            }

            if (removed.Count > 0)
            {
                SaveLocked();
                FileLog.Write("Sticker", $"表情包超出上限 {max}，淘汰 {removed.Count} 张：" +
                                        string.Join("、", removed.Take(5).Select(i => i.Id)));
            }
        }

        return removed;
    }

    private static double Score(StickerRecord item, long now)
    {
        var last = item.LastUsedAt > 0 ? item.LastUsedAt : item.AddedAt;
        var idleDays = Math.Max(0, (now - last) / 86400.0);
        return item.Uses * 3.0 - idleDays;
    }

    /// <summary>
    /// 按语境挑候选。**只挑“已经被模型判定为表情包”的图** ——
    /// 否则可能把聊天截图/广告当表情包发出去（线上踩过）。
    /// 关键词重合度为主，掺一点随机避免每次都发同一张。
    /// </summary>
    public List<StickerRecord> PickCandidates(string query, int count, int excludeUsedWithinSeconds = -1)
    {
        if (count <= 0)
        {
            return new List<StickerRecord>();
        }

        var tokens = Tokenize(query);
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var random = Random.Shared;

        // 排除窗口自适应：库里图多就多排除一会儿，图少就短一点 ——
        // 否则 29 张的库配上 10 分钟窗口，常常“全被排除”被迫吃兜底（兜底以前又按相关度转回熟脸 ✗）。
        var excludeWindow = excludeUsedWithinSeconds < 0
            ? (int)Math.Clamp(_items.Count * 60, 300, 3600)
            : excludeUsedWithinSeconds;

        lock (_gate)
        {
            var usable = _items.Where(i => i.IsSticker == true).ToList();
            var scored = usable
                .Select(item => (Item: item, Score: RelevanceScore(item, tokens) + random.NextDouble() * 1.2))
                .Where(x => x.Item.LastUsedAt == 0 || now - x.Item.LastUsedAt > excludeWindow)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item)
                .ToList();

            // 最近都发过 → 退一步不再排除，否则会“无图可选”（库里就那几张时很常见）
            if (scored.Count < count)
            {
                var extra = usable
                    .Where(i => !scored.Contains(i))
                    // 兜底**不能**再按相关度来一遍 —— 那正是“老发同一张”的来源 ✗。
                    // 改按「最久没用」优先（相同时间再掺随机），让冷门图先上场。
                    .Select(item => (Item: item, Last: item.LastUsedAt, Rnd: random.NextDouble()))
                    .OrderBy(x => x.Last)
                    .ThenBy(x => x.Rnd)
                    .Select(x => x.Item);
                scored.AddRange(extra);
            }

            return scored.Take(count).Select(Clone).ToList();
        }
    }

    /// <summary>
    /// 关键词重合度：命中标签权重最高，其次说明文字。
    ///
    /// ⚠ 2026-09-21 修“老是同一张”：这里以前是 <c>item.Uses * 0.05</c> —— **用得多得分高**，
    /// 直接形成正反馈（线上 60 次只发了 9 个 id，29 张里 17 张 30 天没碰过 ✗）。
    /// 现在反过来：用得多**减分**、久没用**加分** —— 冷门图才有机会轮到。
    /// </summary>
    private static double RelevanceScore(StickerRecord item, List<string> tokens)
    {
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var last = item.LastUsedAt > 0 ? item.LastUsedAt : item.AddedAt;
        var idleDays = Math.Max(0, (now - last) / 86400.0);
        var freshness = Math.Min(idleDays, 30) * 0.05;   // 越久没用越吃香（上限 +1.5）
        var wear = -Math.Min(item.Uses, 40) * 0.06;      // 用得越多越扣分（下限 -2.4）

        if (tokens.Count == 0)
        {
            return freshness + wear;
        }

        double score = freshness + wear;
        foreach (var token in tokens)
        {
            if (item.Tags.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase)))
            {
                score += 2.0;
            }
            else if (!string.IsNullOrWhiteSpace(item.Desc) &&
                     item.Desc.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1.0;
            }
        }

        return score;
    }

    /// <summary>
    /// 把一段中文/英文文本切成检索词。中文没有空格，这里用二元组（bigram）近似，
    /// 对“短句里找情绪关键词”这个场景足够，也不需要引入分词库。
    /// </summary>
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        foreach (Match match in Regex.Matches(text, @"[\u4e00-\u9fa5]+|[A-Za-z0-9]+"))
        {
            var word = match.Value;
            if (word.Length == 0)
            {
                continue;
            }

            if (word[0] <= 0x7f)
            {
                tokens.Add(word.ToLowerInvariant());
                continue;
            }

            if (word.Length == 1)
            {
                tokens.Add(word);
                continue;
            }

            for (var i = 0; i + 2 <= word.Length; i++)
            {
                tokens.Add(word.Substring(i, 2));
            }
        }

        return tokens.Distinct().Take(24).ToList();
    }

    private void SaveLocked()
    {
        try
        {
            // 库里的索引与内存列表整体对账（几十~几千条，和以前“整份重写 index.json”一样便宜）：
            // upsert 全部 + 删掉库里多出来的（被删除/被淘汰的那些）。
            var ids = _items.Select(i => i.Id).ToList();
            AppDatabase.Write(conn =>
            {
                foreach (var item in _items)
                {
                    AppDatabase.Exec(conn, """
                        INSERT INTO stickers(id, hash, file, ext, bytes, added_unix, last_used_unix, uses,
                                             from_uid, from_group, description, tags, is_sticker, described, describe_attempts)
                        VALUES($id, $h, $f, $ext, $b, $a, $lu, $u, $fu, $fg, $d, $tags, $is, $de, $da)
                        ON CONFLICT(id) DO UPDATE SET
                            hash = excluded.hash, file = excluded.file, ext = excluded.ext, bytes = excluded.bytes,
                            last_used_unix = excluded.last_used_unix, uses = excluded.uses,
                            from_uid = excluded.from_uid, from_group = excluded.from_group,
                            description = excluded.description, tags = excluded.tags, is_sticker = excluded.is_sticker,
                            described = excluded.described, describe_attempts = excluded.describe_attempts
                        """,
                        ("$id", item.Id), ("$h", item.Hash), ("$f", item.File), ("$ext", item.Ext),
                        ("$b", item.Bytes), ("$a", item.AddedAt), ("$lu", item.LastUsedAt), ("$u", item.Uses),
                        ("$fu", item.FromUid), ("$fg", item.FromGroup), ("$d", item.Desc),
                        ("$tags", item.Tags is { Count: > 0 } ? JsonSerializer.Serialize(item.Tags) : null),
                        ("$is", item.IsSticker is null ? null : (item.IsSticker.Value ? 1 : 0)),
                        ("$de", item.Described ? 1 : 0), ("$da", item.DescribeAttempts));
                }

                AppDatabase.Exec(conn, "CREATE TEMP TABLE IF NOT EXISTS _keep_sticker(id TEXT PRIMARY KEY)");
                AppDatabase.Exec(conn, "DELETE FROM _keep_sticker");
                foreach (var id in ids)
                {
                    AppDatabase.Exec(conn, "INSERT OR IGNORE INTO _keep_sticker(id) VALUES($id)", ("$id", id));
                }

                AppDatabase.Exec(conn, "DELETE FROM stickers WHERE id NOT IN (SELECT id FROM _keep_sticker)");
                AppDatabase.Exec(conn, "DELETE FROM _keep_sticker");
            });
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包索引写库失败：{ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略：索引已经删掉，孤立文件不影响功能
        }
    }

    private static string NormalizeExt(string? ext)
    {
        var clean = (ext ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        return clean switch
        {
            "jpg" or "jpeg" => "jpg",
            "gif" => "gif",
            "webp" => "webp",
            "bmp" => "bmp",
            _ => "png"
        };
    }

    private static StickerRecord Clone(StickerRecord item) => new()
    {
        Id = item.Id,
        Hash = item.Hash,
        File = item.File,
        AbsolutePath = item.AbsolutePath,
        Ext = item.Ext,
        Bytes = item.Bytes,
        AddedAt = item.AddedAt,
        LastUsedAt = item.LastUsedAt,
        Uses = item.Uses,
        FromUid = item.FromUid,
        FromGroup = item.FromGroup,
        Desc = item.Desc,
        Tags = new List<string>(item.Tags),
        IsSticker = item.IsSticker,
        Described = item.Described,
        DescribeAttempts = item.DescribeAttempts
    };
}
