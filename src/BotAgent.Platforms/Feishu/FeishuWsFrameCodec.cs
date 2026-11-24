using System.Text;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed record FeishuWsHeader(string Key, string Value);

public sealed record FeishuWsFrame(
    ulong SeqId,
    ulong LogId,
    int Service,
    int Method,
    IReadOnlyList<FeishuWsHeader> Headers,
    string? PayloadEncoding,
    string? PayloadType,
    byte[] Payload,
    string? LogIdNew = null)
{
    public string? GetHeader(string key)
    {
        for (var i = 0; i < Headers.Count; i++)
        {
            if (string.Equals(Headers[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return Headers[i].Value;
        }
        return null;
    }
}

/// <summary>
/// 飞书官方长连接 <c>pbbp2.Frame</c> 纯托管 Protobuf 二进制编解码器 + 多分片重组缓存（零第三方依赖）。
/// </summary>
public static class FeishuWsFrameCodec
{
    public const int MethodControl = 0;
    public const int MethodData = 1;
    public const int MaxFrameBytes = 1024 * 1024;

    public static byte[] Encode(FeishuWsFrame frame)
    {
        using var ms = new MemoryStream(256 + (frame.Payload?.Length ?? 0));
        WriteTag(ms, 1, 0); WriteVarint(ms, frame.SeqId);
        WriteTag(ms, 2, 0); WriteVarint(ms, frame.LogId);
        WriteTag(ms, 3, 0); WriteVarint(ms, (ulong)(long)frame.Service);
        WriteTag(ms, 4, 0); WriteVarint(ms, (ulong)(long)frame.Method);
        if (frame.Headers is { Count: > 0 })
        {
            foreach (var h in frame.Headers)
            {
                using var hs = new MemoryStream(64);
                WriteStringField(hs, 1, h.Key ?? string.Empty);
                WriteStringField(hs, 2, h.Value ?? string.Empty);
                var hb = hs.ToArray();
                WriteTag(ms, 5, 2);
                WriteVarint(ms, (ulong)hb.Length);
                ms.Write(hb, 0, hb.Length);
            }
        }
        if (!string.IsNullOrEmpty(frame.PayloadEncoding))
            WriteStringField(ms, 6, frame.PayloadEncoding!);
        if (!string.IsNullOrEmpty(frame.PayloadType))
            WriteStringField(ms, 7, frame.PayloadType!);
        if (frame.Payload is { Length: > 0 })
        {
            WriteTag(ms, 8, 2);
            WriteVarint(ms, (ulong)frame.Payload.Length);
            ms.Write(frame.Payload, 0, frame.Payload.Length);
        }
        if (!string.IsNullOrEmpty(frame.LogIdNew))
            WriteStringField(ms, 9, frame.LogIdNew!);
        return ms.ToArray();
    }

    public static bool TryDecode(ReadOnlySpan<byte> data, out FeishuWsFrame? frame)
    {
        frame = null;
        if (data.Length == 0 || data.Length > MaxFrameBytes)
            return false;

        ulong seqId = 0, logId = 0;
        int service = 0, method = 0;
        var headers = new List<FeishuWsHeader>(8);
        string? payloadEncoding = null, payloadType = null, logIdNew = null;
        byte[] payload = [];

        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out var tag) || tag == 0)
                return false;
            var rawField = tag >> 3;
            if (rawField == 0 || rawField > 1024)
                return false;
            var fieldNumber = (int)rawField;
            var wireType = (int)(tag & 0x7);

            switch (fieldNumber)
            {
                case 1 when wireType == 0:
                    if (!TryReadVarint(data, ref offset, out seqId)) return false;
                    break;
                case 2 when wireType == 0:
                    if (!TryReadVarint(data, ref offset, out logId)) return false;
                    break;
                case 3 when wireType == 0:
                    if (!TryReadVarint(data, ref offset, out var svc)) return false;
                    service = (int)svc;
                    break;
                case 4 when wireType == 0:
                    if (!TryReadVarint(data, ref offset, out var mtd)) return false;
                    method = (int)mtd;
                    break;
                case 5 when wireType == 2:
                    if (!TryReadBytes(data, ref offset, out var headerSlice)) return false;
                    if (!TryDecodeHeader(headerSlice, out var header)) return false;
                    if (headers.Count < 64) headers.Add(header);
                    break;
                case 6 when wireType == 2:
                    if (!TryReadBytes(data, ref offset, out var encSlice)) return false;
                    payloadEncoding = Encoding.UTF8.GetString(encSlice);
                    break;
                case 7 when wireType == 2:
                    if (!TryReadBytes(data, ref offset, out var typeSlice)) return false;
                    payloadType = Encoding.UTF8.GetString(typeSlice);
                    break;
                case 8 when wireType == 2:
                    if (!TryReadBytes(data, ref offset, out var bodySlice)) return false;
                    payload = bodySlice.ToArray();
                    break;
                case 9 when wireType == 2:
                    if (!TryReadBytes(data, ref offset, out var logSlice)) return false;
                    logIdNew = Encoding.UTF8.GetString(logSlice);
                    break;
                default:
                    if (!TrySkipField(data, ref offset, wireType)) return false;
                    break;
            }
        }

        frame = new FeishuWsFrame(seqId, logId, service, method, headers, payloadEncoding, payloadType, payload, logIdNew);
        return true;
    }

    private static bool TryDecodeHeader(ReadOnlySpan<byte> data, out FeishuWsHeader header)
    {
        string key = string.Empty, val = string.Empty;
        var offset = 0;
        while (offset < data.Length)
        {
            if (!TryReadVarint(data, ref offset, out var tag) || tag == 0)
            {
                header = default!;
                return false;
            }
            var rawField = tag >> 3;
            if (rawField == 0 || rawField > 1024)
            {
                header = default!;
                return false;
            }
            var field = (int)rawField;
            var wire = (int)(tag & 0x7);
            if (field == 1 && wire == 2)
            {
                if (!TryReadBytes(data, ref offset, out var ks)) { header = default!; return false; }
                key = Encoding.UTF8.GetString(ks);
            }
            else if (field == 2 && wire == 2)
            {
                if (!TryReadBytes(data, ref offset, out var vs)) { header = default!; return false; }
                val = Encoding.UTF8.GetString(vs);
            }
            else if (!TrySkipField(data, ref offset, wire))
            {
                header = default!;
                return false;
            }
        }
        header = new FeishuWsHeader(key, val);
        return true;
    }

    private static void WriteStringField(Stream s, int fieldNumber, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(s, fieldNumber, 2);
        WriteVarint(s, (ulong)bytes.Length);
        s.Write(bytes, 0, bytes.Length);
    }

    private static void WriteTag(Stream s, int fieldNumber, int wireType)
        => WriteVarint(s, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int offset, out ulong result)
    {
        result = 0;
        var shift = 0;
        while (offset < data.Length && shift < 64)
        {
            var b = data[offset++];
            if (shift == 63 && b > 1) return false;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> data, ref int offset, out ReadOnlySpan<byte> slice)
    {
        slice = default;
        if (!TryReadVarint(data, ref offset, out var len) || len > (ulong)(data.Length - offset))
            return false;
        var n = (int)len;
        slice = data.Slice(offset, n);
        offset += n;
        return true;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> data, ref int offset, int wireType)
    {
        switch (wireType)
        {
            case 0:
                return TryReadVarint(data, ref offset, out _);
            case 1:
                if (data.Length - offset < 8) return false;
                offset += 8;
                return true;
            case 2:
                return TryReadBytes(data, ref offset, out _);
            case 5:
                if (data.Length - offset < 4) return false;
                offset += 4;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>有界分片重组器（<c>sum &gt; 1</c> 的大事件载荷按 <c>message_id + seq</c> 合并，总上限 1 MiB）。</summary>
internal sealed class FeishuWsFragmentReassembler
{
    private const int MaxInFlightMessages = 32;
    private const int MaxFragmentsPerMessage = 64;
    private static readonly TimeSpan FragmentTtl = TimeSpan.FromSeconds(15);

    private sealed class Entry(int total)
    {
        public byte[]?[] Parts { get; } = new byte[]?[total];
        public int ReceivedCount { get; set; }
        public int TotalBytes { get; set; }
        public DateTimeOffset CreatedAt { get; } = Clock.UtcNow;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public bool TryCombine(FeishuWsFrame frame, out byte[]? completePayload)
    {
        completePayload = null;
        var sumRaw = frame.GetHeader("sum");
        if (string.IsNullOrEmpty(sumRaw) || !int.TryParse(sumRaw, out var sum) || sum <= 1)
        {
            completePayload = frame.Payload;
            return true;
        }

        var msgId = frame.GetHeader("message_id");
        var seqRaw = frame.GetHeader("seq");
        if (string.IsNullOrWhiteSpace(msgId) || !int.TryParse(seqRaw, out var seq)
            || sum > MaxFragmentsPerMessage || seq < 0 || seq >= sum)
            return false;

        var now = Clock.UtcNow;
        lock (_gate)
        {
            if (_entries.TryGetValue(msgId, out var staleCheck) && now - staleCheck.CreatedAt > FragmentTtl)
                _entries.Remove(msgId);

            if (_entries.Count >= MaxInFlightMessages && !_entries.ContainsKey(msgId))
            {
                var expired = _entries.Where(kv => now - kv.Value.CreatedAt > FragmentTtl).Select(kv => kv.Key).ToList();
                foreach (var k in expired) _entries.Remove(k);
                if (_entries.Count >= MaxInFlightMessages)
                {
                    var oldestKey = _entries.MinBy(kv => kv.Value.CreatedAt).Key;
                    _entries.Remove(oldestKey);
                }
            }

            if (!_entries.TryGetValue(msgId, out var entry) || entry.Parts.Length != sum)
            {
                entry = new Entry(sum);
                _entries[msgId] = entry;
            }

            if (entry.Parts[seq] is null)
            {
                if (entry.TotalBytes + frame.Payload.Length > FeishuWsFrameCodec.MaxFrameBytes)
                {
                    _entries.Remove(msgId);
                    return false;
                }
                entry.Parts[seq] = frame.Payload;
                entry.TotalBytes += frame.Payload.Length;
                entry.ReceivedCount++;
            }

            if (entry.ReceivedCount < sum)
                return false;

            var merged = new byte[entry.TotalBytes];
            var pos = 0;
            for (var i = 0; i < sum; i++)
            {
                var part = entry.Parts[i]!;
                Buffer.BlockCopy(part, 0, merged, pos, part.Length);
                pos += part.Length;
            }
            _entries.Remove(msgId);
            completePayload = merged;
            return true;
        }
    }
}
