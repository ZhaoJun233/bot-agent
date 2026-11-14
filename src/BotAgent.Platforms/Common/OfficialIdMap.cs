using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed partial class OfficialIdMap : IOfficialIdMap
{
    private readonly string? _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _toAlias = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _toOriginal = new();
    private readonly long _aliasBase;
    private readonly long _aliasLimit;
    private readonly bool _strictAllocation;
    private readonly Func<string, bool>? _validateOriginal;

    public OfficialIdMap(string path)
        : this(path, Channels.AliasBase, Channels.AliasBase + 2_000_000_000_000L, false)
    {
    }

    /// <summary>
    /// Every disk-backed binding is persisted before return. The historical durable argument
    /// selects strict hash allocation (used by Feishu), not whether disk errors may be ignored.
    /// Official legacy mode preserves existing aliases and its original linear collision probing.
    /// A null path is an explicitly ephemeral map for offline probes only.
    /// </summary>
    public OfficialIdMap(string? path, long aliasBase, long aliasLimit, bool durable, Func<string, bool>? validateOriginal = null)
        : this(path, aliasBase, aliasLimit, durable, validateOriginal, typedOfficial: false)
    {
    }

    private OfficialIdMap(string? path, long aliasBase, long aliasLimit, bool durable,
        Func<string, bool>? validateOriginal, bool typedOfficial)
    {
        _path = path;
        if (aliasBase <= 0 || aliasLimit <= aliasBase) throw new ArgumentOutOfRangeException(nameof(aliasBase));
        _aliasBase = aliasBase;
        _aliasLimit = aliasLimit;
        _strictAllocation = durable;
        _validateOriginal = validateOriginal;
        _typedOfficial = typedOfficial;
        Load();
    }

    /// <summary>拿 openid 的别名；没有就分配一个并落盘。</summary>
    public long AliasFor(string original)
    {
        if (_typedOfficial) throw new InvalidOperationException("identity_type_required");
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

    /// <summary>Read an existing binding without allocating an identity or writing the map.</summary>
    public bool TryGetAlias(string original, out long alias)
    {
        alias = 0;
        if (string.IsNullOrWhiteSpace(original)) return false;
        lock (_gate)
        {
            return _toAlias.TryGetValue(original, out alias);
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
        var candidate = CandidateFor(original);
        // Durable identities cannot depend on arrival order when a candidate collides.
        if (_strictAllocation && _toOriginal.ContainsKey(candidate))
            throw new InvalidOperationException("Identity alias collision; explicit recovery required.");
        var first = candidate;
        while (_toOriginal.ContainsKey(candidate))
        {
            candidate++;
            if (candidate >= _aliasLimit)
            {
                candidate = _aliasBase; // 绕回去找
            }
            if (candidate == first)
                throw new InvalidOperationException("Identity alias range exhausted.");
        }
        return candidate;
    }

    private long CandidateFor(string original)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(original));
        var range = _aliasLimit - _aliasBase;
        var slot = _strictAllocation
            ? (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash) % (ulong)range)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash) % Math.Min(range, 1_000_000_000_000L);
        return _aliasBase + slot;
    }

    private void Load()
    {
        if (_path is null) return;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(_path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        // Only an absent file is a new map. Unreadable, malformed or ambiguous state is
        // retained for explicit recovery; rebuilding it could reassign a persisted identity.
        try
        {
            // Keep the old reader's BOM support, but bind migration previews to original bytes.
            using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            using var document = JsonDocument.Parse(reader.ReadToEnd());
            ReadEntries(ValidateDocument(document.RootElement));
            if (_typedOfficial) ReadOfficialIndex(document.RootElement);
            _snapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            // Do not put raw identity text from parser errors into caller diagnostics.
            throw new InvalidDataException("Invalid identity map document.");
        }
    }

    private JsonElement ValidateDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid identity map document.");
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!properties.Add(property.Name) || property.Name is not ("note" or "aliasBase" or "map")
                && !(_typedOfficial && property.Name == "officialIdentities"))
                throw new InvalidDataException("Unsupported or ambiguous identity map document.");
            if (property.Name == "aliasBase" && (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt64(out var aliasBase) || aliasBase != _aliasBase))
                throw new InvalidDataException("Identity map range mismatch.");
        }
        if (!root.TryGetProperty("map", out var entries) || entries.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Missing identity map.");
        return entries;
    }

    private void ReadEntries(JsonElement entries)
    {
        foreach (var entry in entries.EnumerateObject())
        {
            var original = entry.Name;
            if (string.IsNullOrWhiteSpace(original) || entry.Value.ValueKind != JsonValueKind.Number
                || !entry.Value.TryGetInt64(out var alias) || alias < _aliasBase || alias >= _aliasLimit
                || _toAlias.ContainsKey(original) || _toOriginal.ContainsKey(alias)
                || (_validateOriginal is not null && !_validateOriginal(original))
                || (_strictAllocation && alias != CandidateFor(original)))
                throw new InvalidDataException("Invalid or ambiguous identity alias.");
            _toAlias.Add(original, alias);
            _toOriginal.Add(alias, original);
        }
    }

    /// <summary>Compatibility hook: every successful allocation is already committed.</summary>
    public void Flush() { }

    private void SaveLocked()
    {
        if (_path is null) return;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var map = new JsonObject();
        foreach (var (original, alias) in _toAlias) map[original] = alias;
        var doc = new JsonObject
        {
            ["note"] = _strictAllocation ? "平台稳定身份 → 别名号（只存标识，不存聊天内容）" : "官方通道 openid → 别名号（只存标识，不存聊天内容）",
            ["aliasBase"] = _aliasBase,
            ["map"] = map,
        };
        if (_typedOfficial) doc["officialIdentities"] = WriteOfficialIndex();
        var text = doc.ToJsonString();
        var tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(text);
                writer.Flush();
                output.Flush(flushToDisk: true);
            }
            File.Move(tmp, _path, overwrite: true);
            _snapshotSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        }
        finally
        {
            // Cleanup must not change the observed publication result.
            try { File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
