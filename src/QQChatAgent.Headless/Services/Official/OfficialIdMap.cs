using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QQChatAgent.Services.Qq;

namespace QQChatAgent.Services.Official;

/// <summary>
/// 官方通道的「别名号」台账：openid（字符串）⇄ 数字号段（<see cref="Channels.AliasBase"/> 起步）。
///
/// 为什么要这层：QQ 开放平台的会话标识是 openid 字符串（群 <c>group_openid</c>、人 <c>user_openid</c>），
/// 而机器人整条链路（白名单、会话 key、面板按钮、消息 id 引用）全是按 <c>long</c> 走的 ——
/// 与其把 <c>long</c> 改成 <c>string</c> 掀掉半个工程（并弄脏老库），不如给 openid 发一个稳定的数字别名：
///   • 别名从 <see cref="Channels.AliasBase"/> 往上发（8e15 起步），真实 QQ 号/群号永远撞不上 → 通道一眼可判；
///   • 映射落盘（<c>data/official-ids.json</c>），重启/重装后同一个 openid 还是同一个别名 ——
///     否则历史会话、白名单、长期记忆全会“换人”。
///   • 消息 id 也一样（官方那边是字符串），同样映射成 long，引用回复才能照旧工作。
///
/// 注意：这个表**只存标识与时间**，不存任何聊天内容。
/// </summary>
public sealed class OfficialIdMap
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _toAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _toOriginal = new();
    private long _next;
    private bool _dirty;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    public OfficialIdMap(string path)
    {
        _path = path;
        _next = Channels.AliasBase;
        Load();
    }

    /// <summary>拿 openid 的别名；没有就分配一个并落盘。</summary>
    public long AliasFor(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            return 0;
        }

        lock (_gate)
        {
            if (_toAlias.TryGetValue(original, out var existing))
            {
                return existing;
            }

            var alias = Allocate(original);
            _toAlias[original] = alias;
            _toOriginal[alias] = original;
            SaveLocked();
            return alias;
        }
    }

    /// <summary>别名还原成 openid（发消息、上传富媒体都要用它）。</summary>
    public string? OriginalOf(long alias)
    {
        lock (_gate)
        {
            return _toOriginal.TryGetValue(alias, out var original) ? original : null;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _toAlias.Count;
            }
        }
    }

    /// <summary>
    /// 分配规则：先按 openid 的哈希在号段里挑一个候选位（同一群永远优先落在同一位，
    /// 即使表丢了也能大概率复现），被占了就往后线性探测。
    /// </summary>
    private long Allocate(string original)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(original));
        var slot = BitConverter.ToUInt32(hash, 0) % 1_000_000_000_000L; // 号段里 1e12 个位置，够用
        var candidate = Channels.AliasBase + slot;
        var guard = 0;
        while (_toOriginal.ContainsKey(candidate) && guard++ < 1_000_000)
        {
            candidate++;
            if (candidate >= Channels.AliasBase + 2_000_000_000_000L)
            {
                candidate = Channels.AliasBase; // 绕回去找
            }
        }

        if (candidate >= _next)
        {
            _next = candidate + 1;
        }

        return candidate;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = JsonNode.Parse(File.ReadAllText(_path));
            var entries = json?["map"]?.AsObject();
            if (entries is null)
            {
                return;
            }

            foreach (var (original, node) in entries)
            {
                if (node is null)
                {
                    continue;
                }

                var alias = node.GetValue<long>();
                if (alias >= Channels.AliasBase)
                {
                    _toAlias[original] = alias;
                    _toOriginal[alias] = original;
                }
            }

            if (_toOriginal.Count > 0)
            {
                _next = _toOriginal.Keys.Max() + 1;
            }
        }
        catch (Exception)
        {
            // 表坏了不能拖垮启动：丢掉重建（别名会变，但会话 key 里的老别名仍能当普通号用）
            _toAlias.Clear();
            _toOriginal.Clear();
        }
    }

    private void SaveLocked()
    {
        // 落盘不必每次分配都写（openid 分配是偶发事件）；1 秒一次 + 进程退出时收尾即可
        _dirty = true;
        if (DateTimeOffset.Now - _lastSave < TimeSpan.FromSeconds(1))
        {
            return;
        }

        SaveNowLocked();
    }

    /// <summary>立即落盘（进程退出前调一次）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_dirty)
            {
                SaveNowLocked();
            }
        }
    }

    private void SaveNowLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var map = new JsonObject();
            foreach (var (original, alias) in _toAlias)
            {
                map[original] = alias;
            }

            var doc = new JsonObject
            {
                ["note"] = "官方通道 openid → 别名号（只存标识，不存聊天内容）",
                ["aliasBase"] = Channels.AliasBase,
                ["map"] = map,
            };

            var tmp = _path + ".tmp";
            // 不传 options：JsonNode.ToJsonString() 用内置默认（自己 new 一个没带 TypeInfoResolver 的
            // JsonSerializerOptions 会在首次使用时抛 read-only/resolver 错误 —— SettingsStore 那边就踩过）。
            File.WriteAllText(tmp, doc.ToJsonString());
            File.Move(tmp, _path, overwrite: true);
            _dirty = false;
            _lastSave = DateTimeOffset.Now;
        }
        catch (Exception)
        {
            // 写不进去不影响运行（内存里那份还在），下次再试
        }
    }
}
