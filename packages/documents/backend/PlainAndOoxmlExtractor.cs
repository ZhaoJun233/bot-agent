using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BotAgent.Services.Documents;

namespace BotAgent.Packages.Documents;

/// <summary>纯文本、代码、HTML 及 Office OpenXML（DOCX / XLSX / PPTX）零第三方依赖提取器。</summary>
internal static class PlainAndOoxmlExtractor
{
    private const int CharactersPerTextPage = 2400;
    private static int _codePagesRegistered;

    public static DocumentExtractResult ExtractPlainText(ReadOnlySpan<byte> data, string ext, string? section, int maxChars)
    {
        if (HasBinaryNullRatio(data))
            return new(false, string.Empty, 0, false, "binary", "binary_content_unsupported");

        var decoded = DecodeText(data);
        if (ext is ".html" or ".htm")
            decoded = StripHtml(decoded);

        var normalized = NormalizeWhitespaceLines(decoded);
        if (string.IsNullOrWhiteSpace(normalized))
            return new(false, string.Empty, 0, false, "text", "document_empty");

        var totalUnits = Math.Max(1, (normalized.Length + CharactersPerTextPage - 1) / CharactersPerTextPage);
        if (!DocumentSafetyGuards.TryParseSectionRange(section, totalUnits, out var from, out var to))
            return new(false, string.Empty, totalUnits, false, "text", "invalid_section_range");

        var startIdx = Math.Min(normalized.Length, (from - 1) * CharactersPerTextPage);
        var endIdx = Math.Min(normalized.Length, to * CharactersPerTextPage);
        var slice = normalized[startIdx..endIdx];
        var truncated = slice.Length > maxChars || endIdx < normalized.Length;
        if (slice.Length > maxChars)
            slice = slice[..maxChars] + "\n…（已达单次提取上限，可通过 section 参数继续读取后续分段）";

        return new(true, slice, totalUnits, truncated, "text");
    }

    public static DocumentExtractResult ExtractDocx(ReadOnlyMemory<byte> data, string? section, int maxChars)
    {
        Dictionary<string, byte[]> entries;
        try
        {
            entries = DocumentSafetyGuards.ReadBoundedZipEntries(
                data, name => name.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase), out var err);
            if (err is not null) return new(false, string.Empty, 0, false, "docx", err);
        }
        catch { return new(false, string.Empty, 0, false, "docx", "invalid_docx_archive"); }

        if (!entries.TryGetValue("word/document.xml", out var docXml))
            return new(false, string.Empty, 0, false, "docx", "docx_body_missing");

        XDocument doc;
        try { doc = DocumentSafetyGuards.LoadSafeXml(docXml); }
        catch { return new(false, string.Empty, 0, false, "docx", "docx_xml_rejected"); }

        var blocks = new List<string>();
        foreach (var element in doc.Root?.Descendants() ?? [])
        {
            var local = element.Name.LocalName;
            if (local == "p" && element.Ancestors().All(a => a.Name.LocalName != "tc"))
            {
                var line = ReadWordParagraph(element);
                if (!string.IsNullOrWhiteSpace(line)) blocks.Add(line);
            }
            else if (local == "tr")
            {
                var cells = element.Elements().Where(e => e.Name.LocalName == "tc")
                    .Select(tc => string.Join(" ", tc.Elements().Where(p => p.Name.LocalName == "p").Select(ReadWordParagraph).Where(s => s.Length > 0)))
                    .ToArray();
                if (cells.Any(c => c.Length > 0))
                    blocks.Add("| " + string.Join(" | ", cells) + " |");
            }
        }

        return PaginateUnits(blocks, 25, section, maxChars, "docx", "段组");
    }

    public static DocumentExtractResult ExtractXlsx(ReadOnlyMemory<byte> data, string? section, int maxChars)
    {
        Dictionary<string, byte[]> entries;
        try
        {
            entries = DocumentSafetyGuards.ReadBoundedZipEntries(data, name =>
                name.Equals("xl/workbook.xml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase), out var err);
            if (err is not null) return new(false, string.Empty, 0, false, "xlsx", err);
        }
        catch { return new(false, string.Empty, 0, false, "xlsx", "invalid_xlsx_archive"); }

        try
        {
            var shared = LoadSharedStrings(entries);
            var sheetNames = LoadWorkbookSheetNames(entries);
            var sheets = entries.Keys
                .Where(k => k.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy( ExtractTrailingNumber)
                .ToList();
            if (sheets.Count == 0)
                return new(false, string.Empty, 0, false, "xlsx", "xlsx_sheets_missing");

            var renderedSheets = new List<string>(sheets.Count);
            for (var i = 0; i < sheets.Count; i++)
            {
                var title = i < sheetNames.Count ? sheetNames[i] : $"Sheet{i + 1}";
                var sheetDoc = DocumentSafetyGuards.LoadSafeXml(entries[sheets[i]]);
                var sb = new StringBuilder();
                sb.AppendLine($"### 工作表 {i + 1}: {title}");
                var rowCount = 0;
                foreach (var row in sheetDoc.Descendants().Where(e => e.Name.LocalName == "row"))
                {
                    if (++rowCount > 200) { sb.AppendLine("…（后续行已省略）"); break; }
                    var vals = row.Elements().Where(c => c.Name.LocalName == "c")
                        .Select(c => ReadXlsxCell(c, shared)).ToArray();
                    if (vals.Any(v => v.Length > 0))
                        sb.AppendLine(string.Join("\t", vals));
                }
                renderedSheets.Add(sb.ToString().TrimEnd());
            }
            return PaginateUnits(renderedSheets, 1, section, maxChars, "xlsx", "工作表");
        }
        catch { return new(false, string.Empty, 0, false, "xlsx", "xlsx_xml_rejected"); }
    }

    public static DocumentExtractResult ExtractPptx(ReadOnlyMemory<byte> data, string? section, int maxChars)
    {
        Dictionary<string, byte[]> entries;
        try
        {
            entries = DocumentSafetyGuards.ReadBoundedZipEntries(data, name =>
                name.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase), out var err);
            if (err is not null) return new(false, string.Empty, 0, false, "pptx", err);
        }
        catch { return new(false, string.Empty, 0, false, "pptx", "invalid_pptx_archive"); }

        var slideKeys = entries.Keys.OrderBy(ExtractTrailingNumber).ToList();
        if (slideKeys.Count == 0)
            return new(false, string.Empty, 0, false, "pptx", "pptx_slides_missing");

        try
        {
            var slides = new List<string>(slideKeys.Count);
            for (var i = 0; i < slideKeys.Count; i++)
            {
                var doc = DocumentSafetyGuards.LoadSafeXml(entries[slideKeys[i]]);
                var texts = doc.Descendants().Where(e => e.Name.LocalName == "t")
                    .Select(e => e.Value.Trim()).Where(t => t.Length > 0);
                slides.Add($"### 幻灯片 {i + 1}\n" + string.Join(" ", texts));
            }
            return PaginateUnits(slides, 1, section, maxChars, "pptx", "页");
        }
        catch { return new(false, string.Empty, 0, false, "pptx", "pptx_xml_rejected"); }
    }

    private static DocumentExtractResult PaginateUnits(
        IReadOnlyList<string> items, int groupSize, string? section, int maxChars, string format, string unitLabel)
    {
        var units = new List<string>();
        for (var i = 0; i < items.Count; i += groupSize)
            units.Add(string.Join("\n", items.Skip(i).Take(groupSize)));

        if (units.Count == 0 || units.All(string.IsNullOrWhiteSpace))
            return new(false, string.Empty, 0, false, format, "document_empty");

        if (!DocumentSafetyGuards.TryParseSectionRange(section, units.Count, out var from, out var to))
            return new(false, string.Empty, units.Count, false, format, "invalid_section_range");

        var combined = string.Join("\n\n", units.Skip(from - 1).Take(to - from + 1)).Trim();
        var truncated = combined.Length > maxChars || to < units.Count;
        if (combined.Length > maxChars)
            combined = combined[..maxChars] + $"\n…（共 {units.Count} {unitLabel}，可通过 section 参数翻阅）";

        return new(true, combined, units.Count, truncated, format);
    }

    private static string ReadWordParagraph(XElement paragraph)
    {
        var sb = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            var name = node.Name.LocalName;
            if (name == "t") sb.Append(node.Value);
            else if (name == "tab") sb.Append('\t');
            else if (name is "br" or "cr") sb.Append('\n');
        }
        return sb.ToString().Trim();
    }

    private static List<string> LoadSharedStrings(Dictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue("xl/sharedStrings.xml", out var bytes)) return [];
        var doc = DocumentSafetyGuards.LoadSafeXml(bytes);
        return doc.Descendants().Where(e => e.Name.LocalName == "si")
            .Select(si => string.Concat(si.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)))
            .ToList();
    }

    private static List<string> LoadWorkbookSheetNames(Dictionary<string, byte[]> entries)
    {
        if (!entries.TryGetValue("xl/workbook.xml", out var bytes)) return [];
        var doc = DocumentSafetyGuards.LoadSafeXml(bytes);
        return doc.Descendants().Where(e => e.Name.LocalName == "sheet")
            .Select(s => s.Attribute("name")?.Value ?? "Sheet").ToList();
    }

    private static string ReadXlsxCell(XElement cell, IReadOnlyList<string> shared)
    {
        var type = cell.Attribute("t")?.Value;
        if (type == "inlineStr")
            return string.Concat(cell.Descendants().Where(t => t.Name.LocalName == "t").Select(t => t.Value)).Trim();
        var raw = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? string.Empty;
        if (type == "s" && int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Count)
            return shared[idx].Trim();
        return raw.Trim();
    }

    private static int ExtractTrailingNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    private static bool HasBinaryNullRatio(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
            return false;
        var sample = data[..Math.Min(data.Length, 4096)];
        var nulls = 0;
        foreach (var b in sample) if (b == 0) nulls++;
        return sample.Length > 0 && nulls * 20 > sample.Length;
    }

    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data[3..]);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return Encoding.Unicode.GetString(data[2..]);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data[2..]);

        try
        {
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(data);
        }
        catch
        {
            try
            {
                if (Interlocked.Exchange(ref _codePagesRegistered, 1) == 0)
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(54936).GetString(data);
            }
            catch
            {
                return Encoding.UTF8.GetString(data);
            }
        }
    }

    private static string StripHtml(string html)
    {
        var withoutBlocks = Regex.Replace(html, @"<(script|style|noscript)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
        var withBreaks = Regex.Replace(withoutBlocks, @"<(br|p|div|li|tr|h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        var plain = Regex.Replace(withBreaks, @"<[^>]+>", " ");
        return WebUtility.HtmlDecode(plain);
    }

    private static string NormalizeWhitespaceLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.TrimEnd()).ToList();
        var sb = new StringBuilder();
        var blankRun = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (++blankRun <= 1) sb.AppendLine();
                continue;
            }
            blankRun = 0;
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
}
