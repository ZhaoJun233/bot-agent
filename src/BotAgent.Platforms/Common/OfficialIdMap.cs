using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Services.Qq;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

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
public sealed class OfficialIdMap : IOfficialIdMap
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _toAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _toOriginal = new();
    private long _next;
    private bool _dirty;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private readonly long _aliasBase;
    private readonly long _aliasLimit;
    private readonly bool _durable;
    private readonly Func<string, bool>? _validateOriginal;

    public OfficialIdMap(string path)
        : this(path, Channels.AliasBase, Channels.AliasBase + 2_000_000_000_000L, false)
    {
    }

    /// <summary>
    /// Reuse the bidirectional store for a separate platform range. Durable mode writes every
    /// binding synchronously, validates restored candidates, and rejects collisions instead of probing.
    /// A null path is an explicitly ephemeral map for offline probes only.
    /// </summary>
    public OfficialIdMap(string? path, long aliasBase, long aliasLimit, bool durable, Func<string, bool>? validateOriginal = null)
    {
        _path = path;
        if (aliasBase <= 0 || aliasLimit <= aliasBase) throw new ArgumentOutOfRangeException(nameof(aliasBase));
        _aliasBase = aliasBase;
        _aliasLimit = aliasLimit;
        _durable = durable;
        _validateOriginal = validateOriginal;
        _next = aliasBase;
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

            if (_validateOriginal is not null && !_validateOriginal(original))
                throw new InvalidDataException("Invalid identity key.");

            var alias = Allocate(original);
            _toAlias[original] = alias;
            _toOriginal[alias] = original;
            try { SaveLocked(); }
            catch
            {
                _toAlias.Remove(original);
                _toOriginal.Remove(alias);
                throw;
            }
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
        var slot = _durable
            ? (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)(_aliasLimit - _aliasBase))
            : BitConverter.ToUInt32(hash, 0) % 1_000_000_000_000L;
        var candidate = _aliasBase + slot;
        // Durable identities cannot depend on arrival order when a candidate collides.
        if (_durable && _toOriginal.ContainsKey(candidate))
            throw new InvalidOperationException("Identity alias collision; explicit recovery required.");
        var guard = 0;
        while (_toOriginal.ContainsKey(candidate) && guard++ < 1_000_000)
        {
            candidate++;
            if (candidate >= _aliasLimit)
            {
                candidate = _aliasBase; // 绕回去找
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
        if (_path is null) return;
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
                if (_durable) throw new InvalidDataException("Missing identity map.");
                return;
            }

            foreach (var (original, node) in entries)
            {
                if (node is null)
                {
                    if (_durable) throw new InvalidDataException("Invalid identity map entry.");
                    continue;
                }

                var alias = node.GetValue<long>();
                if (_durable && (alias < _aliasBase || alias >= _aliasLimit || _toOriginal.ContainsKey(alias)
                    || (_validateOriginal is not null && !_validateOriginal(original)) || alias != Allocate(original)))
                    throw new InvalidDataException("Invalid or ambiguous identity alias.");
                if (alias >= _aliasBase)
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
        catch (Exception) when (!_durable)
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
        if (!_durable && Clock.Now - _lastSave < TimeSpan.FromSeconds(1))
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
        if (_path is null) { _dirty = false; return; }
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
                ["note"] = _durable ? "平台稳定身份 → 别名号（只存标识，不存聊天内容）" : "官方通道 openid → 别名号（只存标识，不存聊天内容）",
                ["aliasBase"] = _aliasBase,
                ["map"] = map,
            };

            var tmp = _path + ".tmp";
            // 不传 options：JsonNode.ToJsonString() 用内置默认（自己 new 一个没带 TypeInfoResolver 的
            // JsonSerializerOptions 会在首次使用时抛 read-only/resolver 错误 —— SettingsStore 那边就踩过）。
            File.WriteAllText(tmp, doc.ToJsonString());
            File.Move(tmp, _path, overwrite: true);
            _dirty = false;
            _lastSave = Clock.Now;
        }
        catch (Exception) when (!_durable)
        {
            // 写不进去不影响运行（内存里那份还在），下次再试
        }
    }
}
