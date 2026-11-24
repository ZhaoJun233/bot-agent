using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Knowledge;

namespace BotAgent.Storage;

/// <summary>
/// 同会话轻量知识库持久化与 BM25 检索引擎（Schema v15 兼容）：
/// 每个会话复合作用域独立存入 <c>AppDatabase</c> <c>meta</c> 表（<c>knowledge:scope:v1:&lt;sha256&gt;</c>），
/// 零新增建表/迁移 SQL，天然物理隔离；单会话硬上限 256 文档 / 4096 切片。
/// </summary>
public sealed class KnowledgeStore : IKnowledgeStore
{
    public const int AbsoluteMaxDocsPerScope = 256;
    public const int AbsoluteMaxChunksPerScope = 4096;
    public const int AbsoluteMaxInputChars = 120_000;
    public const int AbsoluteMaxScopeBytes = 1024 * 1024;
    private const int SnapshotSchemaVersion = 1;
    private const string MetaPrefix = "knowledge:scope:v1:";

    private readonly KnowledgeBm25Index _bm25 = new();
    private readonly object _writeGate = new();

    private sealed record PersistedDocument(
        string DocId,
        string Title,
        string SourceKind,
        int ChunkCount,
        int TotalChars,
        long UpdatedUnix);

    private sealed record ScopeSnapshot(
        int SchemaVersion,
        [property: JsonPropertyName("docs")] List<PersistedDocument> Docs,
        [property: JsonPropertyName("chunks")] List<KnowledgeChunkEntry> Chunks,
        [property: JsonPropertyName("rev")] long Revision = 0)
    {
        public static ScopeSnapshot Empty() => new(SnapshotSchemaVersion, new List<PersistedDocument>(), new List<KnowledgeChunkEntry>(), 0);
    }

    private static string ScopeMetaKey(string compositeScope)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(compositeScope));
        return MetaPrefix + Convert.ToHexStringLower(hash);
    }

    public KnowledgeSaveResult SaveDocument(
        string compositeScope,
        string title,
        string content,
        string sourceKind,
        KnowledgeOptions options)
    {
        if (string.IsNullOrWhiteSpace(compositeScope))
            return new(false, "scope_required");

        var cleanTitle = SanitizeInline(title, 80);
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return new(false, "title_required");

        var cleanBody = SanitizeBody(content);
        if (string.IsNullOrWhiteSpace(cleanBody))
            return new(false, "content_required");

        var cleanKind = string.IsNullOrWhiteSpace(sourceKind) ? "note" : SanitizeInline(sourceKind, 24);
        var maxDocs = Math.Clamp(options.MaxDocsPerScope, 1, AbsoluteMaxDocsPerScope);
        var maxChunks = Math.Clamp(options.MaxChunksPerScope, 16, AbsoluteMaxChunksPerScope);
        var chunkChars = Math.Clamp(options.MaxChunkChars, 200, 1600);
        var chunkTexts = SplitChunks(cleanBody, chunkChars);
        if (chunkTexts.Count == 0)
            return new(false, "content_required");

        var docId = BuildDocId(cleanTitle, cleanKind);
        var nowUnix = Clock.UtcNow.ToUnixTimeSeconds();
        var docItem = new KnowledgeDocumentItem(docId, cleanTitle, cleanKind, chunkTexts.Count, cleanBody.Length, nowUnix);

        var key = ScopeMetaKey(compositeScope);
        lock (_writeGate)
        {
            KnowledgeSaveResult? outcome = null;
            AppDatabase.Write(conn =>
            {
                var rawSnapshot = AppDatabase.GetMetaInTransaction(conn, key);
                var snapshot = ParseSnapshot(rawSnapshot, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(rawSnapshot))
                    AppDatabase.SetMetaInTransaction(conn, key + ":corrupt", rawSnapshot);
                var replacedOldChunks = snapshot.Chunks.RemoveAll(c => string.Equals(c.DocId, docId, StringComparison.Ordinal));
                var existed = snapshot.Docs.RemoveAll(d => string.Equals(d.DocId, docId, StringComparison.Ordinal)) > 0;

                if (!existed && snapshot.Docs.Count >= maxDocs)
                {
                    outcome = new(false, "doc_quota_exceeded");
                    return;
                }
                if (snapshot.Chunks.Count + chunkTexts.Count > maxChunks)
                {
                    outcome = new(false, "chunk_quota_exceeded");
                    return;
                }

                snapshot.Docs.Add(new PersistedDocument(docId, cleanTitle, cleanKind, chunkTexts.Count, cleanBody.Length, nowUnix));
                for (var i = 0; i < chunkTexts.Count; i++)
                {
                    snapshot.Chunks.Add(new KnowledgeChunkEntry(docId, cleanTitle, i, chunkTexts[i], nowUnix));
                }

                var updated = snapshot with { Revision = checked(snapshot.Revision + 1) };
                var payloadJson = JsonSerializer.Serialize(updated);
                if (Encoding.UTF8.GetByteCount(payloadJson) > AbsoluteMaxScopeBytes)
                {
                    outcome = new(false, "scope_payload_too_large");
                    return;
                }
                AppDatabase.SetMetaInTransaction(conn, key, payloadJson);
                _ = replacedOldChunks;
                outcome = new(true, existed ? "updated" : "saved", docItem);
            });

            _bm25.Invalidate(compositeScope);
            return outcome ?? new(false, "save_failed");
        }
    }

    public KnowledgeSearchHit[] Search(string compositeScope, string query, int topK)
    {
        if (string.IsNullOrWhiteSpace(compositeScope) || string.IsNullOrWhiteSpace(query))
            return [];
        var k = Math.Clamp(topK, 1, 10);
        if (_bm25.TrySearchCached(compositeScope, query, k, out var cachedHits))
            return cachedHits;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var gen = _bm25.CurrentGeneration(compositeScope);
            var snapshot = LoadSnapshot(compositeScope);
            if (snapshot.Chunks.Count == 0) return [];
            var hits = _bm25.Search(compositeScope, snapshot.Revision, gen, snapshot.Chunks, query, k);
            if (hits is not null) return hits;
        }
        return [];
    }

    public KnowledgeDocumentItem[] ListDocuments(string compositeScope)
    {
        if (string.IsNullOrWhiteSpace(compositeScope)) return [];
        var snapshot = LoadSnapshot(compositeScope);
        return snapshot.Docs
            .OrderByDescending(d => d.UpdatedUnix)
            .ThenBy(d => d.DocId, StringComparer.Ordinal)
            .Select(d => new KnowledgeDocumentItem(d.DocId, d.Title, d.SourceKind, d.ChunkCount, d.TotalChars, d.UpdatedUnix))
            .ToArray();
    }

    public bool ForgetDocument(string compositeScope, string docId)
    {
        if (string.IsNullOrWhiteSpace(compositeScope) || string.IsNullOrWhiteSpace(docId))
            return false;
        var cleanId = docId.Trim();
        var key = ScopeMetaKey(compositeScope);
        lock (_writeGate)
        {
            var removed = false;
            AppDatabase.Write(conn =>
            {
                var snapshot = ParseSnapshot(AppDatabase.GetMetaInTransaction(conn, key));
                var docCount = snapshot.Docs.RemoveAll(d => string.Equals(d.DocId, cleanId, StringComparison.OrdinalIgnoreCase));
                var chunkCount = snapshot.Chunks.RemoveAll(c => string.Equals(c.DocId, cleanId, StringComparison.OrdinalIgnoreCase));
                removed = docCount > 0 || chunkCount > 0;
                if (removed)
                {
                    var updated = snapshot with { Revision = checked(snapshot.Revision + 1) };
                    var encoded = updated.Docs.Count == 0 && updated.Chunks.Count == 0
                        ? string.Empty
                        : JsonSerializer.Serialize(updated);
                    AppDatabase.SetMetaInTransaction(conn, key, encoded);
                }
            });

            if (removed)
                _bm25.Invalidate(compositeScope);
            return removed;
        }
    }

    public void ClearScope(string compositeScope)
    {
        if (string.IsNullOrWhiteSpace(compositeScope)) return;
        var key = ScopeMetaKey(compositeScope);
        lock (_writeGate)
        {
            AppDatabase.Write(conn =>
            {
                if (AppDatabase.GetMetaInTransaction(conn, key) is not null)
                    AppDatabase.SetMetaInTransaction(conn, key, string.Empty);
            });
            _bm25.Invalidate(compositeScope);
        }
    }

    private ScopeSnapshot LoadSnapshot(string compositeScope)
    {
        try
        {
            using var conn = AppDatabase.Open();
            return ParseSnapshot(AppDatabase.GetMetaInTransaction(conn, ScopeMetaKey(compositeScope)));
        }
        catch
        {
            return ScopeSnapshot.Empty();
        }
    }

    private static ScopeSnapshot ParseSnapshot(string? raw) => ParseSnapshot(raw, out _);

    private static ScopeSnapshot ParseSnapshot(string? raw, out bool corrupted)
    {
        corrupted = false;
        if (string.IsNullOrWhiteSpace(raw)) return ScopeSnapshot.Empty();
        try
        {
            var parsed = JsonSerializer.Deserialize<ScopeSnapshot>(raw);
            if (parsed is null || parsed.SchemaVersion != SnapshotSchemaVersion)
            {
                corrupted = true;
                return ScopeSnapshot.Empty();
            }
            return new ScopeSnapshot(
                SnapshotSchemaVersion,
                parsed.Docs ?? new List<PersistedDocument>(),
                parsed.Chunks ?? new List<KnowledgeChunkEntry>(),
                parsed.Revision);
        }
        catch
        {
            corrupted = true;
            return ScopeSnapshot.Empty();
        }
    }

    private static string BuildDocId(string title, string sourceKind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceKind}|{title}"));
        return "kb_" + Convert.ToHexStringLower(hash)[..8];
    }

    private static string SanitizeInline(string raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var escaped = MessageMarkers.EscapeExternalControlTags(raw);
        var sb = new StringBuilder(Math.Min(escaped.Length, maxChars));
        foreach (var ch in escaped)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
            if (sb.Length >= maxChars) break;
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString().Trim();
    }

    private static string SanitizeBody(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var cap = AdjustSurrogateLength(raw, 0, Math.Min(raw.Length, AbsoluteMaxInputChars));
        var clipped = raw.Length > cap ? raw[..cap] : raw;
        var escaped = MessageMarkers.EscapeExternalControlTags(clipped);
        var sb = new StringBuilder(escaped.Length);
        foreach (var ch in escaped)
        {
            if (ch is '\r' or '\n' or '\t' || !char.IsControl(ch))
                sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static int AdjustSurrogateLength(string text, int start, int length)
    {
        if (length <= 0 || start + length >= text.Length) return length;
        return char.IsHighSurrogate(text[start + length - 1]) && length > 1 ? length - 1 : length;
    }

    internal static List<string> SplitChunks(string text, int maxChunkChars)
    {
        var chunks = new List<string>();
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0) return chunks;

        var current = new StringBuilder();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                FlushCurrent();
                continue;
            }

            if (line.Length > maxChunkChars)
            {
                FlushCurrent();
                for (var offset = 0; offset < line.Length; )
                {
                    var len = Math.Min(maxChunkChars, line.Length - offset);
                    var safeLen = AdjustSurrogateLength(line, offset, len);
                    var slice = line.Substring(offset, safeLen).Trim();
                    if (slice.Length > 0) chunks.Add(slice);
                    offset += safeLen;
                }
                continue;
            }

            if (current.Length > 0 && current.Length + 1 + line.Length > maxChunkChars)
                FlushCurrent();
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }

        FlushCurrent();
        return chunks;

        void FlushCurrent()
        {
            if (current.Length == 0) return;
            var block = current.ToString().Trim();
            if (block.Length > 0) chunks.Add(block);
            current.Clear();
        }
    }
}
