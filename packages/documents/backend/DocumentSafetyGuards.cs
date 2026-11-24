using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace BotAgent.Packages.Documents;

/// <summary>
/// 文档解析安全守卫：拒绝可执行二进制魔数、防御 ZIP 膨胀炸弹与 XML 外部实体（XXE / Billion Laughs）。
/// </summary>
internal static class DocumentSafetyGuards
{
    public const int AbsoluteMaxInputBytes = 10 * 1024 * 1024;
    public const int MaxZipEntries = 512;
    public const long MaxUnpackedBytes = 32L * 1024 * 1024;
    public const int MaxInflationRatio = 20;

    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".dylib", ".msi", ".bat", ".cmd", ".com", ".scr", ".sys", ".drv", ".bin"
    };

    public static bool IsBlockedExecutable(string fileName, ReadOnlySpan<byte> data)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        if (!string.IsNullOrEmpty(ext) && BlockedExtensions.Contains(ext))
            return true;
        if (data.Length >= 2 && data[0] == (byte)'M' && data[1] == (byte)'Z')
            return true;
        if (data.Length >= 4 && data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F')
            return true;
        if (data.Length >= 4)
        {
            var magic = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            if (magic is 0xCAFEBABEu or 0xFEEDFACEu or 0xFEEDFACFu or 0xCEFAEDFEu or 0xCFFAEDFEu)
                return true;
        }
        return false;
    }

    public static bool TryParseSectionRange(string? section, int totalUnits, out int startOneBased, out int endOneBased)
    {
        startOneBased = 1;
        endOneBased = Math.Max(1, totalUnits);
        if (string.IsNullOrWhiteSpace(section)) return true;
        var text = section.Trim();
        var dash = text.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(text, out var single) || single < 1) return false;
            startOneBased = Math.Min(single, Math.Max(1, totalUnits));
            endOneBased = startOneBased;
            return true;
        }
        var left = text[..dash].Trim();
        var right = text[(dash + 1)..].Trim();
        if (!int.TryParse(left, out var from) || !int.TryParse(right, out var to) || from < 1 || to < from)
            return false;
        startOneBased = Math.Min(from, Math.Max(1, totalUnits));
        endOneBased = Math.Min(to, Math.Max(1, totalUnits));
        return true;
    }

    public static XDocument LoadSafeXml(byte[] xmlBytes)
    {
        using var ms = new MemoryStream(xmlBytes, writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = MaxUnpackedBytes
        };
        using var reader = XmlReader.Create(ms, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    public static Dictionary<string, byte[]> ReadBoundedZipEntries(
        ReadOnlyMemory<byte> archiveBytes, Func<string, bool> includeEntry, out string? errorCode)
    {
        errorCode = null;
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var ms = new MemoryStream(archiveBytes.ToArray(), writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        if (zip.Entries.Count > MaxZipEntries)
        {
            errorCode = "zip_too_many_entries";
            return map;
        }

        long cumulative = 0;
        var buffer = new byte[8192];
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || !includeEntry(entry.FullName))
                continue;
            var compressed = Math.Max(1L, entry.CompressedLength);
            using var stream = entry.Open();
            using var output = new MemoryStream();
            long entryRead = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryRead += read;
                cumulative += read;
                if (cumulative > MaxUnpackedBytes)
                {
                    errorCode = "zip_unpacked_limit_exceeded";
                    return map;
                }
                if (entryRead > 65536 && entryRead > compressed * MaxInflationRatio)
                {
                    errorCode = "zip_bomb_rejected";
                    return map;
                }
                output.Write(buffer, 0, read);
            }
            map[entry.FullName.Replace('\\', '/')] = output.ToArray();
        }
        return map;
    }
}
