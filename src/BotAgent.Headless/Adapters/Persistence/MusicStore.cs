using BotAgent.Domain.Music;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// “听过的歌”台账：SQLite 的 <c>heard_songs</c> 表（原来是 data/music/listened.json）。
/// 存它是为了两件事：① 同一首歌被反复分享时不再重复下载/解码（省流量也省 CPU）；
/// ② 面板/日志能看清机器人到底听过什么、分析过哪些。
/// 内存里保留一份列表当读缓存（几十条），写操作直接落库。
/// </summary>
public sealed class MusicStore : IMusicRepository
{
    private readonly Func<int> _maxItems;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly List<HeardSong> _songs = [];

    public MusicStore(Func<int> maxItems, Action<string> log)
    {
        _maxItems = maxItems;
        _log = log;
        Load();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _songs.Count;
            }
        }
    }

    /// <summary>最近听过的歌（面板用）。</summary>
    public IReadOnlyList<HeardSong> Recent(int count)
    {
        lock (_gate)
        {
            return _songs.OrderByDescending(s => s.LastHeard).Take(Math.Max(1, count)).ToList();
        }
    }

    public HeardSong? Get(string key)
    {
        lock (_gate)
        {
            return _songs.FirstOrDefault(s => s.Key == key);
        }
    }

    /// <summary>记一笔：同键更新（次数 +1、刷新时间），否则新增；超出上限按最久没听到的淘汰。</summary>
    public HeardSong Remember(HeardSong song)
    {
        lock (_gate)
        {
            var existing = _songs.FirstOrDefault(s => s.Key == song.Key);
            if (existing is null)
            {
                _songs.Add(song);
                existing = song;
            }
            else
            {
                existing.LastHeard = song.LastHeard;
                existing.HeardCount++;
                existing.Title = string.IsNullOrWhiteSpace(song.Title) ? existing.Title : song.Title;
                existing.Artist = string.IsNullOrWhiteSpace(song.Artist) ? existing.Artist : song.Artist;
                existing.Album = string.IsNullOrWhiteSpace(song.Album) ? existing.Album : song.Album;
                existing.DurationSeconds = song.DurationSeconds > 0 ? song.DurationSeconds : existing.DurationSeconds;
                existing.Features = string.IsNullOrWhiteSpace(song.Features) ? existing.Features : song.Features;
                existing.LyricExcerpt = string.IsNullOrWhiteSpace(song.LyricExcerpt) ? existing.LyricExcerpt : song.LyricExcerpt;
            }

            var max = Math.Max(10, _maxItems());
            var evicted = new List<string>();
            if (_songs.Count > max)
            {
                foreach (var old in _songs.OrderBy(s => s.LastHeard).Take(_songs.Count - max).ToList())
                {
                    _songs.Remove(old);
                    evicted.Add(old.Key);
                }
            }

            Save(existing, evicted);
            return existing;
        }
    }

    private void Load()
    {
        try
        {
            var loaded = AppDatabase.Query("""
                SELECT key, platform, song_id, title, artist, album, duration_seconds, features, lyric_excerpt,
                       first_heard_unix, last_heard_unix, heard_count
                FROM heard_songs
                ORDER BY last_heard_unix DESC
                """, r => new HeardSong
            {
                Key = AppDatabase.Str(r, "key") ?? string.Empty,
                Platform = AppDatabase.Str(r, "platform") ?? string.Empty,
                SongId = AppDatabase.Str(r, "song_id") ?? string.Empty,
                Title = AppDatabase.Str(r, "title") ?? string.Empty,
                Artist = AppDatabase.Str(r, "artist") ?? string.Empty,
                Album = AppDatabase.Str(r, "album") ?? string.Empty,
                DurationSeconds = AppDatabase.Double(r, "duration_seconds"),
                Features = AppDatabase.Str(r, "features") ?? string.Empty,
                LyricExcerpt = AppDatabase.Str(r, "lyric_excerpt") ?? string.Empty,
                FirstHeard = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "first_heard_unix")),
                LastHeard = DateTimeOffset.FromUnixTimeSeconds(AppDatabase.Long(r, "last_heard_unix")),
                HeardCount = AppDatabase.Int(r, "heard_count")
            });

            if (loaded.Count > 0)
            {
                _songs.AddRange(loaded);
                _log($"[Music] 已加载听过的歌 {loaded.Count} 首");
            }
        }
        catch (Exception ex)
        {
            _log($"[Music] 读取听过的歌失败（按空处理）: {ex.Message}");
        }
    }

    private void Save(HeardSong song, IReadOnlyList<string> evictedKeys)
    {
        try
        {
            AppDatabase.Write(conn =>
            {
                AppDatabase.Exec(conn, """
                    INSERT INTO heard_songs(key, platform, song_id, title, artist, album, duration_seconds,
                                            features, lyric_excerpt, first_heard_unix, last_heard_unix, heard_count)
                    VALUES($k, $p, $sid, $title, $artist, $album, $dur, $f, $l, $fh, $lh, $c)
                    ON CONFLICT(key) DO UPDATE SET
                        title = excluded.title, artist = excluded.artist, album = excluded.album,
                        duration_seconds = excluded.duration_seconds, features = excluded.features,
                        lyric_excerpt = excluded.lyric_excerpt, last_heard_unix = excluded.last_heard_unix,
                        heard_count = excluded.heard_count
                    """,
                    ("$k", song.Key), ("$p", song.Platform), ("$sid", song.SongId), ("$title", song.Title),
                    ("$artist", song.Artist), ("$album", song.Album), ("$dur", song.DurationSeconds),
                    ("$f", song.Features), ("$l", song.LyricExcerpt),
                    ("$fh", song.FirstHeard.ToUnixTimeSeconds()), ("$lh", song.LastHeard.ToUnixTimeSeconds()),
                    ("$c", song.HeardCount));

                foreach (var key in evictedKeys)
                {
                    AppDatabase.Exec(conn, "DELETE FROM heard_songs WHERE key = $k", ("$k", key));
                }
            });
        }
        catch (Exception ex)
        {
            _log($"[Music] 保存听过的歌失败: {ex.Message}");
        }
    }
}
