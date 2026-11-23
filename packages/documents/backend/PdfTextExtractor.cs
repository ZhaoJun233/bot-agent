using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using BotAgent.Services.Documents;

namespace BotAgent.Packages.Documents;

/// <summary>
/// 零第三方 NuGet 的纯 .NET BCL PDF 文本层流式提取器：
/// 支持未压缩与 /FlateDecode 内容流、BT..ET 文本块算子（Tj / TJ / ' / "）、UTF-16BE Hex 串及 /ToUnicode CMap。
/// 加密或无文本层扫描件如实返回结构化原因码。
/// </summary>
internal static class PdfTextExtractor
{
    private const int MaxStreamUnpackedBytes = 16 * 1024 * 1024;
    private const int MaxTotalUnpackedBytes = 32 * 1024 * 1024;

    public static DocumentExtractResult Extract(ReadOnlySpan<byte> pdfBytes, string? section, int maxChars)
    {
        if (pdfBytes.Length < 8 || !StartsWithAscii(pdfBytes, "%PDF-"))
            return new(false, string.Empty, 0, false, "pdf", "invalid_pdf_header");

        var latin = Encoding.Latin1.GetString(pdfBytes);
        if (ContainsEncryptionDictionary(latin))
            return new(false, string.Empty, 0, false, "pdf", "pdf_encrypted");

        var rawArray = pdfBytes.ToArray();
        var streams = ExtractDecodedStreams(rawArray, latin, out var bombError);
        if (bombError is not null)
            return new(false, string.Empty, 0, false, "pdf", bombError);

        var cmap = BuildCMapFromStreams(streams);
        var pageTexts = new List<string>();
        foreach (var streamText in streams)
        {
            if (streamText.Contains("beginbfchar", StringComparison.Ordinal)
                || streamText.Contains("beginbfrange", StringComparison.Ordinal))
                continue;
            if (!streamText.Contains("BT", StringComparison.Ordinal))
                continue;
            var extracted = ExtractTextBlocks(streamText, cmap);
            if (!string.IsNullOrWhiteSpace(extracted))
                pageTexts.Add(extracted);
        }

        if (pageTexts.Count == 0)
            return new(false, string.Empty, 0, false, "pdf", "pdf_no_text_layer");

        if (!DocumentSafetyGuards.TryParseSectionRange(section, pageTexts.Count, out var from, out var to))
            return new(false, string.Empty, pageTexts.Count, false, "pdf", "invalid_section_range");

        var sb = new StringBuilder();
        for (var i = from; i <= to; i++)
        {
            sb.AppendLine($"### 第 {i} 节/页");
            sb.AppendLine(pageTexts[i - 1]);
            sb.AppendLine();
        }

        var combined = sb.ToString().Trim();
        var truncated = combined.Length > maxChars || to < pageTexts.Count;
        if (combined.Length > maxChars)
            combined = combined[..maxChars] + $"\n…（共 {pageTexts.Count} 节/页，可通过 section 参数继续读取）";

        return new(true, combined, pageTexts.Count, truncated, "pdf");
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> bytes, string prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (bytes[i] != (byte)prefix[i]) return false;
        return true;
    }

    private static bool ContainsEncryptionDictionary(string latin)
        => Regex.IsMatch(latin, @"(?<![A-Za-z0-9])/Encrypt\b");

    private static List<string> ExtractDecodedStreams(byte[] raw, string latin, out string? errorCode)
    {
        errorCode = null;
        var decoded = new List<string>();
        long totalUnpacked = 0;
        var pos = 0;
        while (pos < latin.Length)
        {
            var streamToken = latin.IndexOf("stream", pos, StringComparison.Ordinal);
            if (streamToken < 0) break;
            if (streamToken > 0 && char.IsLetterOrDigit(latin[streamToken - 1]))
            {
                pos = streamToken + 6;
                continue;
            }
            var dataStart = streamToken + 6;
            if (dataStart < latin.Length && latin[dataStart] == '\r') dataStart++;
            if (dataStart < latin.Length && latin[dataStart] == '\n') dataStart++;
            else if (latin[streamToken + 6] != '\n')
            {
                pos = streamToken + 6;
                continue;
            }

            var endStream = latin.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (endStream < 0) break;
            var dictStart = Math.Max(0, streamToken - 512);
            var dictHeader = latin[dictStart..streamToken];
            var dataEnd = endStream;
            while (dataEnd > dataStart && (raw[dataEnd - 1] == (byte)'\n' || raw[dataEnd - 1] == (byte)'\r'))
                dataEnd--;

            var length = Math.Max(0, dataEnd - dataStart);
            if (length > 0 && !dictHeader.Contains("/Image", StringComparison.Ordinal) && !dictHeader.Contains("/DCTDecode", StringComparison.Ordinal))
            {
                var flate = dictHeader.Contains("/FlateDecode", StringComparison.Ordinal);
                var unpacked = flate ? TryInflate(raw, dataStart, length, out errorCode) : raw.AsSpan(dataStart, length).ToArray();
                if (errorCode is not null) return decoded;
                if (unpacked is { Length: > 0 })
                {
                    totalUnpacked += unpacked.Length;
                    if (totalUnpacked > MaxTotalUnpackedBytes)
                    {
                        errorCode = "pdf_unpacked_limit_exceeded";
                        return decoded;
                    }
                    decoded.Add(Encoding.Latin1.GetString(unpacked));
                }
            }
            pos = endStream + 9;
        }
        return decoded;
    }

    private static byte[]? TryInflate(byte[] raw, int offset, int count, out string? errorCode)
    {
        errorCode = null;
        var inflated = InflateWith(new MemoryStream(raw, offset, count, writable: false), useZlib: true, out errorCode);
        if (inflated is not null || errorCode is not null) return inflated;
        return count > 2
            ? InflateWith(new MemoryStream(raw, offset + 2, count - 2, writable: false), useZlib: false, out errorCode)
            : null;
    }

    private static byte[]? InflateWith(MemoryStream source, bool useZlib, out string? errorCode)
    {
        errorCode = null;
        try
        {
            using Stream decompressor = useZlib
                ? new ZLibStream(source, CompressionMode.Decompress)
                : new DeflateStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buf = new byte[8192];
            long total = 0;
            int read;
            while ((read = decompressor.Read(buf, 0, buf.Length)) > 0)
            {
                total += read;
                if (total > MaxStreamUnpackedBytes)
                {
                    errorCode = "pdf_stream_bomb_rejected";
                    return null;
                }
                if (total > 65536 && total > Math.Max(1L, source.Length) * DocumentSafetyGuards.MaxInflationRatio)
                {
                    errorCode = "pdf_stream_bomb_rejected";
                    return null;
                }
                output.Write(buf, 0, read);
            }
            return output.ToArray();
        }
        catch { return null; }
    }

    private static Dictionary<int, string> BuildCMapFromStreams(IReadOnlyList<string> streams)
    {
        var map = new Dictionary<int, string>();
        foreach (var s in streams)
        {
            if (!s.Contains("beginbf", StringComparison.Ordinal)) continue;
            foreach (Match m in Regex.Matches(s, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                    map[code] = DecodeHexUnicode(m.Groups[2].Value);
            }
            foreach (Match m in Regex.Matches(s, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)
                    && int.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)
                    && int.TryParse(m.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var target)
                    && end >= start && end - start <= 1024)
                {
                    for (var c = start; c <= end; c++)
                        map[c] = char.ConvertFromUtf32(target + (c - start));
                }
            }
        }
        return map;
    }

    private static string ExtractTextBlocks(string content, IReadOnlyDictionary<int, string> cmap)
    {
        var sb = new StringBuilder();
        var cursor = 0;
        while (cursor < content.Length)
        {
            var bt = FindOperator(content, "BT", cursor);
            if (bt < 0) break;
            var et = FindOperator(content, "ET", bt + 2);
            var blockEnd = et > bt ? et : content.Length;
            ParseTextBlock(content.AsSpan(bt + 2, blockEnd - (bt + 2)), cmap, sb);
            sb.Append('\n');
            cursor = et > bt ? et + 2 : content.Length;
        }
        return CleanupExtractedText(sb.ToString());
    }

    private static int FindOperator(string s, string op, int from)
    {
        var idx = from;
        while ((idx = s.IndexOf(op, idx, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(s[idx - 1]);
            var afterIdx = idx + op.Length;
            var afterOk = afterIdx >= s.Length || !char.IsLetterOrDigit(s[afterIdx]);
            if (beforeOk && afterOk) return idx;
            idx = afterIdx;
        }
        return -1;
    }

    private static void ParseTextBlock(ReadOnlySpan<char> block, IReadOnlyDictionary<int, string> cmap, StringBuilder sb)
    {
        var i = 0;
        while (i < block.Length)
        {
            var ch = block[i];
            if (ch == '(')
            {
                var literal = ReadLiteralString(block, ref i);
                sb.Append(literal);
            }
            else if (ch == '<' && (i + 1 >= block.Length || block[i + 1] != '<'))
            {
                var hex = ReadHexString(block, ref i);
                sb.Append(DecodePdfHex(hex, cmap));
            }
            else if (ch == '[')
            {
                i++;
                while (i < block.Length && block[i] != ']')
                {
                    if (block[i] == '(') sb.Append(ReadLiteralString(block, ref i));
                    else if (block[i] == '<' && (i + 1 >= block.Length || block[i + 1] != '<'))
                        sb.Append(DecodePdfHex(ReadHexString(block, ref i), cmap));
                    else i++;
                }
                if (i < block.Length && block[i] == ']') i++;
            }
            else if (ch == 'T' && i + 1 < block.Length && block[i + 1] is 'D' or 'd' or '*' or 'm')
            {
                if (sb.Length > 0 && sb[^1] is not ('\n' or ' ')) sb.Append(' ');
                i += 2;
            }
            else
            {
                i++;
            }
        }
    }

    private static string ReadLiteralString(ReadOnlySpan<char> s, ref int i)
    {
        i++; // skip '('
        var sb = new StringBuilder();
        var depth = 1;
        while (i < s.Length && depth > 0)
        {
            var c = s[i++];
            if (c == '\\' && i < s.Length)
            {
                var next = s[i++];
                if (next is >= '0' and <= '7')
                {
                    var oct = next - '0';
                    for (var k = 0; k < 2 && i < s.Length && s[i] is >= '0' and <= '7'; k++)
                        oct = (oct * 8) + (s[i++] - '0');
                    sb.Append((char)(oct & 0xFF));
                }
                else
                {
                    sb.Append(next switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => next });
                }
            }
            else if (c == '(') { depth++; sb.Append(c); }
            else if (c == ')') { if (--depth > 0) sb.Append(c); }
            else sb.Append(c);
        }
        var raw = sb.ToString();
        if (raw.Length >= 2 && raw[0] == '\xFE' && raw[1] == '\xFF')
        {
            var bytes = Encoding.Latin1.GetBytes(raw[2..]);
            return Encoding.BigEndianUnicode.GetString(bytes);
        }
        return raw;
    }

    private static string ReadHexString(ReadOnlySpan<char> s, ref int i)
    {
        i++; // skip '<'
        var sb = new StringBuilder();
        while (i < s.Length && s[i] != '>')
        {
            if (!char.IsWhiteSpace(s[i])) sb.Append(s[i]);
            i++;
        }
        if (i < s.Length && s[i] == '>') i++;
        if (sb.Length % 2 == 1) sb.Append('0');
        return sb.ToString();
    }

    private static string DecodePdfHex(string hex, IReadOnlyDictionary<int, string> cmap)
    {
        if (hex.StartsWith("FEFF", StringComparison.OrdinalIgnoreCase))
            return DecodeHexUnicode(hex[4..]);
        if (cmap.Count > 0 && hex.Length % 4 == 0)
        {
            var sb4 = new StringBuilder();
            var matchedAny = false;
            for (var i = 0; i + 4 <= hex.Length; i += 4)
            {
                var code = int.Parse(hex.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (cmap.TryGetValue(code, out var mapped)) { sb4.Append(mapped); matchedAny = true; }
                else sb4.Append((char)code);
            }
            if (matchedAny) return sb4.ToString();
        }
        var sb = new StringBuilder();
        for (var i = 0; i + 2 <= hex.Length; i += 2)
        {
            if (int.TryParse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                sb.Append(cmap.TryGetValue(b, out var m) ? m : (char)b);
        }
        return sb.ToString();
    }

    private static string DecodeHexUnicode(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes.Length >= 2 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    private static string CleanupExtractedText(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '\n' or '\t' || !char.IsControl(ch))
                sb.Append(ch);
        }
        return Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ").Trim();
    }
}
