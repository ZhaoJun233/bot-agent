using System.Text.Json;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 机器人的“当前心情”。持久化在 SQLite：<c>mood</c>（模型写的那句话）+ <c>mood_pokes</c>（被戳的时刻）。
///
/// 为什么要它：被戳一戳时，代码原来只有“回话 + 想戳回去就戳回去”这一条路，
/// 群里实测就是**每次被戳都戳回去**（18:40 那次 4 次被戳回了 3 次），看着像个复读机。
/// 群主的要求是“按当前心情决定，不必每次都回戳”。
///
/// 心情由两部分组成，互相配合：
///   ① **客观部分（代码算的）**：最近一段时间被戳了几次 —— 越频繁越烦躁。这是可解释、可测的。
///   ② **主观部分（模型写的）**：模型可以在回复里给 mood 字段，一句话说明它现在的心情
///      （“被戳烦了”“今天心情不错”），代码存下来，下一次提示词里带上。
/// 心情只影响行为倾向（还戳不戳回去、话多话少），不改变安全阀（频率门、防编造那些照旧）。
/// </summary>
public class MoodStore : IMoodRepository
{
    private const string ReadMoodTextSql = "SELECT text, updated_unix FROM mood WHERE id = 1";
    private const string SaveMoodTextSql =
        "INSERT INTO mood(id, text, updated_unix) VALUES(1, $t, $u) " +
        "ON CONFLICT(id) DO UPDATE SET text = excluded.text, updated_unix = excluded.updated_unix";
    private const string SaveMoodPokeSql = "INSERT INTO mood_pokes(at_unix) VALUES($a) ON CONFLICT(at_unix) DO NOTHING";

    internal enum MoodTextWriteState { Rejected = 0, Unchanged = 1, Committed = 2, Failed = 3, Unknown = 4 }
    internal readonly record struct MoodTextWriteResult(MoodTextWriteState State, bool IntentChanged)
    {
        public override string ToString() => "MoodTextWriteResult";
    }
    /// <summary>滚动窗口：只数窗口内被戳过几次，过期的不算（“烦”会随时间自己消）。</summary>
    private static readonly TimeSpan PokeWindow = TimeSpan.FromMinutes(15);

    /// <summary>窗口内被戳到第几次就不想回戳了（代码侧硬拦，不看模型怎么说）。</summary>
    private const int PokeBackLimit = 2;

    /// <summary>
    /// 模型写的心情保留多久。超过这个时间没再更新就认为“过时了”，回落到按被戳次数自动描述 ——
    /// 否则早上写的“被戳烦了”会一路影响整天（面板上还会显示“这是 692 分钟前的心情”，很迷惑）。
    /// 由设置项 MoodTtlSeconds 控制（默认 2 小时，0 = 不过期）。
    ///
    /// 值是**现取**的（<see cref="_ttlSeconds" /> 指着当前生效的那份配置），不缓存一份在本对象上：
    /// 以前谁想改上限都得记得"改完设置顺手同步一次"，忘了那次同步就静默用着旧上限（批次 5 改成自读）。
    /// </summary>
    public TimeSpan Ttl => _ttlSeconds is null
        ? TimeSpan.FromHours(2)
        : TimeSpan.FromSeconds(Math.Max(0, _ttlSeconds()));

    /// <summary>“心情保留多少秒”的来源；null = 用默认的 2 小时（未接配置时）。</summary>
    private readonly Func<int>? _ttlSeconds;

    /// <summary>
    /// <paramref name="ttlSeconds" /> = 当前生效的心情保留秒数（装配点接到配置上）；不传 = 默认 2 小时。
    /// </summary>
    public MoodStore(Func<int>? ttlSeconds = null) => _ttlSeconds = ttlSeconds;

    private readonly object _gate = new();
    private readonly List<long> _pokeTimes = new();
    private bool _loaded;

    /// <summary>模型写的心情（一句话）；空 = 还没写过，用代码按被戳次数描述。</summary>
    public string? Text { get; private set; }

    /// <summary>心情更新时间（用来在提示词里说明“这是多久前的心情”）。</summary>
    public DateTimeOffset? UpdatedAt { get; private set; }

    /// <summary>从库里读一次（进程启动时调用）。参数保留是为了不改调用方签名。</summary>
    public void Load(string dataRoot)
    {
        lock (_gate)
        {
            _pokeTimes.Clear();
            Text = null;
            UpdatedAt = null;

            try
            {
                var row = AppDatabase.Query(ReadMoodTextSql, r => (
                    Text: AppDatabase.Str(r, "text"),
                    Updated: AppDatabase.LongOrNull(r, "updated_unix")));

                if (row.Count > 0)
                {
                    Text = string.IsNullOrWhiteSpace(row[0].Text) ? null : row[0].Text!.Trim();
                    UpdatedAt = row[0].Updated is { } updated
                        ? DateTimeOffset.FromUnixTimeSeconds(updated)
                        : null;
                }

                _pokeTimes.AddRange(AppDatabase.Query("SELECT at_unix FROM mood_pokes ORDER BY at_unix",
                    r => AppDatabase.Long(r, "at_unix")));

                _loaded = true;
                Prune(Clock.Now);
            }
            catch (Exception ex)
            {
                FileLog.Warn("Mood", $"心情状态读取失败（按无心情继续）：{ex.Message}");
            }
        }
    }

    /// <summary>记一次“机器人被戳”（心情的客观来源）。</summary>
    public void RecordPoke(DateTimeOffset at)
    {
        lock (_gate)
        {
            _pokeTimes.Add(at.ToUnixTimeSeconds());
            Prune(at);
            SaveLocked();
        }
    }

    /// <summary>窗口内被戳了几次。</summary>
    public int RecentPokes(DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _pokeTimes.Count;
        }
    }

    /// <summary>
    /// 现在还想不想回戳。被戳太频繁（<see cref="PokeBackLimit"/> 次以内还行）就不想了 ——
    /// 这是“不必每次都戳回去”的代码侧兜底：模型想戳也拦下，并写明理由。
    /// </summary>
    public bool WillPokeBack(DateTimeOffset now, out string reason)
    {
        var count = RecentPokes(now);
        if (count >= PokeBackLimit)
        {
            reason = $"最近 {PokeWindow.TotalMinutes:F0} 分钟已经被戳 {count} 次了，现在没心情回戳";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>模型自己写的心情（空 = 忽略；太长的不收，避免污染提示词）。</summary>
    public bool SetText(string? text, DateTimeOffset now)
        => SetTextObserved(text, now).IntentChanged;

    // The legacy bool describes accepted changed intent, not a durable or applied result.
    internal MoodTextWriteResult SetTextObserved(string? text, DateTimeOffset now)
    {
        if (!TryNormalizeText(text, out var cleaned))
        {
            return new(MoodTextWriteState.Rejected, false);
        }

        lock (_gate)
        {
            if (cleaned == Text)
            {
                return new(MoodTextWriteState.Unchanged, false); // 没变就不写库，也不刷日志
            }

            return WriteTextLocked(cleaned, now);
        }
    }

    internal static bool TryNormalizeText(string? text, out string normalized)
    {
        normalized = text?.Trim().Trim('。', '.', '！', '!', '，', ',') ?? string.Empty;
        return !string.IsNullOrWhiteSpace(normalized) && normalized.Length <= 24;
    }

    // The legacy action ledger needs durable evidence, unlike SetText's accepted-intent bool.
    internal MoodTextWriteResult ApplyLegacyTextObserved(string? normalized, DateTimeOffset now)
    {
        if (normalized is not null && (!TryNormalizeText(normalized, out var cleaned) || cleaned != normalized))
            return new(MoodTextWriteState.Rejected, false);
        lock (_gate)
        {
            if (normalized is not null && normalized == Text)
            {
                try
                {
                    var rows = AppDatabase.Query(ReadMoodTextSql, reader => (
                        Text: AppDatabase.Str(reader, "text"), Updated: AppDatabase.LongOrNull(reader, "updated_unix")));
                    if (rows.Count == 1 && rows[0].Text == normalized)
                    {
                        UpdatedAt = rows[0].Updated is { } stamp ? DateTimeOffset.FromUnixTimeSeconds(stamp) : null;
                        return new(MoodTextWriteState.Unchanged, false);
                    }
                }
                catch (Exception) { return new(MoodTextWriteState.Failed, false); }
            }
            return WriteTextLocked(normalized, now);
        }
    }

    /// <summary>给提示词用的一段话（心情 + 客观依据）。永远有值，模型才知道自己“现在什么状态”。</summary>
    public string Describe(DateTimeOffset now)
    {
        ExpireIfStale(now);

        var count = RecentPokes(now);
        var auto = count switch
        {
            0 => "心情挺平静的",
            1 => "还好，就是刚才被戳了一下",
            _ => $"有点烦，{PokeWindow.TotalMinutes:F0} 分钟内被戳了 {count} 次"
        };

        lock (_gate)
        {
            // A failed durable expiry must not make already expired text visible again.
            var hasText = !string.IsNullOrWhiteSpace(Text) && !IsStale(now);
            if (!hasText) return auto;
            var text = Text!;

            var age = UpdatedAt is null ? string.Empty : $"，这是 {(now - UpdatedAt.Value).TotalMinutes:F0} 分钟前的心情";
            return count > 0 ? $"{text}（最近 {PokeWindow.TotalMinutes:F0} 分钟被戳 {count} 次{age}）" : $"{text}{age}";
        }
    }

    /// <summary>面板显示用：过期的（模型写的那句）要当作没有，否则界面上留着一句早就不算数的心情。</summary>
    public string? CurrentText(DateTimeOffset now)
    {
        ExpireIfStale(now);
        lock (_gate)
        {
            return IsStale(now) ? null : Text;
        }
    }

    /// <summary>
    /// 过期的模型心情当作没写过（顺便写库清掉）。
    /// 为什么在读取时就清：“过期”是个时间事实，不需要定时器；下一次用到它时顺手收掉最省事。
    /// </summary>
    private void ExpireIfStale(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!IsStale(now)) return;
            if (WriteTextLocked(null, UpdatedAt).State == MoodTextWriteState.Committed)
                FileLog.Write("Mood", "主观心情已过期（code=mood_text_expired）→ 回落到按被戳次数自动描述");
        }
    }

    // Caller holds _gate; this is a logical visibility rule independent of durable cleanup success.
    private bool IsStale(DateTimeOffset now)
    {
        var ttl = Ttl;
        return !string.IsNullOrWhiteSpace(Text) && UpdatedAt is { } updated
            && ttl > TimeSpan.Zero && now - updated > ttl;
    }

    /// <summary>面板手改心情（留空 = 交回代码按被戳次数自动描述）。</summary>
    public void Reset(DateTimeOffset now)
        => ResetObserved(now);

    internal MoodTextWriteResult ResetObserved(DateTimeOffset now)
    {
        lock (_gate)
        {
            return WriteTextLocked(null, now);
        }
    }

    private MoodTextWriteResult WriteTextLocked(string? text, DateTimeOffset? updatedAt)
    {
        var callbackComplete = false;
        try
        {
            AppDatabase.Write(conn =>
            {
                WriteTextInTransaction(conn, text, updatedAt);
                WritePokesInTransaction(conn, _pokeTimes);
                callbackComplete = true;
            });
            Text = text;
            UpdatedAt = updatedAt;
            return new(MoodTextWriteState.Committed, true);
        }
        catch (Exception ex)
        {
            var state = callbackComplete ? MoodTextWriteState.Unknown : MoodTextWriteState.Failed;
            var code = callbackComplete ? "mood_text_write_unknown" : "mood_text_write_failed";
            FileLog.Warn("Mood", $"心情文本持久化未确认（code={code},type={ex.GetType().Name}）");
            return new(state, true);
        }
    }

    internal static void WriteTextInTransaction(SqliteConnection connection, string? normalized, DateTimeOffset? updatedAt)
        => AppDatabase.Exec(connection, SaveMoodTextSql, ("$t", normalized), ("$u", updatedAt?.ToUnixTimeSeconds()));

    private static void WritePokesInTransaction(SqliteConnection connection, IEnumerable<long> times)
    {
        foreach (var at in times) AppDatabase.Exec(connection, SaveMoodPokeSql, ("$a", at));
    }

    /// <summary>过期时间点丢掉：窗口外的戳不该继续影响心情。必须在锁内调用。</summary>
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.ToUnixTimeSeconds() - (long)PokeWindow.TotalSeconds;
        var removed = _pokeTimes.RemoveAll(t => t < cutoff);
        if (_pokeTimes.Count > 50)
        {
            _pokeTimes.RemoveRange(0, _pokeTimes.Count - 50);
            removed++;
        }

        if (removed > 0 && _loaded)
        {
            try
            {
                AppDatabase.Write(conn =>
                    AppDatabase.Exec(conn, "DELETE FROM mood_pokes WHERE at_unix < $cutoff", ("$cutoff", cutoff)));
            }
            catch (Exception ex)
            {
                FileLog.Warn("Mood", $"清理过期被戳记录失败：{ex.Message}");
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            AppDatabase.Write(conn =>
            {
                WriteTextInTransaction(conn, Text, UpdatedAt);
                WritePokesInTransaction(conn, _pokeTimes);
            });
        }
        catch (Exception ex)
        {
            FileLog.Warn("Mood", $"心情状态写库失败：{ex.Message}");
        }
    }
}
