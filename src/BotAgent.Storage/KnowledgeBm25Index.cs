using System.Text;
using BotAgent.Services.Knowledge;

namespace BotAgent.Storage;

internal sealed record KnowledgeChunkEntry(
    string DocId,
    string Title,
    int ChunkIndex,
    string Text,
    long UpdatedUnix);

/// <summary>
/// 纯托管零原生依赖的 BM25（k1=1.2, b=0.75）检索索引：
/// 对英文/数字使用 unicode61 小写词项，对中日韩统一表意文字提取 2-gram（单字查询兜底 1-gram），
/// 兼顾中文长短语与英文标识符的高召回。内存索引受 16 个活跃作用域的 LRU 上限约束。
/// </summary>
internal sealed class KnowledgeBm25Index
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int MaxCachedScopes = 16;
    private const long MaxTotalCachedBytes = 8L * 1024 * 1024;

    private sealed class ScopeIndex
    {
        public required long Revision { get; init; }
        public required long Generation { get; init; }
        public required IReadOnlyList<KnowledgeChunkEntry> Chunks { get; init; }
        public required int[] DocLengths { get; init; }
        public required double AvgDocLength { get; init; }
        public required Dictionary<string, List<(int ChunkOrdinal, int TermFreq)>> Postings { get; init; }
        public required LinkedListNode<string> LruNode { get; init; }
        public required long ApproximateBytes { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ScopeIndex> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _cachedBytes;

    public long CurrentGeneration(string compositeScope)
    {
        if (string.IsNullOrWhiteSpace(compositeScope)) return 0;
        lock (_gate) return _generations.GetValueOrDefault(compositeScope, 0);
    }

    public void Invalidate(string compositeScope)
    {
        if (string.IsNullOrWhiteSpace(compositeScope)) return;
        lock (_gate)
        {
            _generations[compositeScope] = _generations.GetValueOrDefault(compositeScope, 0) + 1;
            if (_cache.Remove(compositeScope, out var existing))
            {
                _cachedBytes = Math.Max(0, _cachedBytes - existing.ApproximateBytes);
                _lru.Remove(existing.LruNode);
            }
        }
    }

    public bool TrySearchCached(string compositeScope, string query, int topK, out KnowledgeSearchHit[] hits)
    {
        hits = [];
        if (string.IsNullOrWhiteSpace(compositeScope) || string.IsNullOrWhiteSpace(query) || topK <= 0)
            return false;
        ScopeIndex index;
        lock (_gate)
        {
            var currentGen = _generations.GetValueOrDefault(compositeScope, 0);
            if (!_cache.TryGetValue(compositeScope, out var cached) || cached.Generation != currentGen)
                return false;
            _lru.Remove(cached.LruNode);
            _lru.AddFirst(cached.LruNode);
            index = cached;
        }
        hits = Score(index, query, topK);
        return true;
    }

    public KnowledgeSearchHit[]? Search(
        string compositeScope,
        long revision,
        long expectedGeneration,
        IReadOnlyList<KnowledgeChunkEntry> chunks,
        string query,
        int topK)
    {
        if (chunks.Count == 0 || string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        ScopeIndex? index;
        lock (_gate)
        {
            var currentGen = _generations.GetValueOrDefault(compositeScope, 0);
            if (_cache.TryGetValue(compositeScope, out var cached)
                && cached.Revision == revision && cached.Generation == currentGen)
            {
                _lru.Remove(cached.LruNode);
                _lru.AddFirst(cached.LruNode);
                index = cached;
            }
            else if (expectedGeneration != currentGen)
            {
                return null;
            }
            else
            {
                index = BuildAndInsertLocked(compositeScope, revision, expectedGeneration, chunks);
            }
        }

        return Score(index, query, topK);
    }

    private static KnowledgeSearchHit[] Score(ScopeIndex index, string query, int topK)
    {
        if (index.Chunks.Count == 0) return [];
        var queryTerms = Tokenize(query, includeSingleCjk: true);
        if (queryTerms.Count == 0)
            return [];
        var uniqueQueryTerms = new HashSet<string>(queryTerms, StringComparer.Ordinal);

        var scores = new Dictionary<int, double>();
        var n = index.Chunks.Count;
        var avgdl = Math.Max(1.0, index.AvgDocLength);
        var trimmedQuery = query.Trim();

        foreach (var term in uniqueQueryTerms)
        {
            if (!index.Postings.TryGetValue(term, out var list) || list.Count == 0)
                continue;
            var df = list.Count;
            var idf = Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
            foreach (var (ordinal, tf) in list)
            {
                var dl = index.DocLengths[ordinal];
                var denom = tf + K1 * (1.0 - B + B * (dl / avgdl));
                var score = idf * (tf * (K1 + 1.0)) / Math.Max(0.0001, denom);
                scores[ordinal] = scores.TryGetValue(ordinal, out var cur) ? cur + score : score;
            }
        }

        if (scores.Count == 0)
            return [];

        var hits = new List<KnowledgeSearchHit>(Math.Min(topK, scores.Count));
        foreach (var pair in scores
                     .Select(kv =>
                     {
                         var chunk = index.Chunks[kv.Key];
                         var phraseBoost = !string.IsNullOrEmpty(trimmedQuery)
                             && chunk.Text.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                             ? 1.25
                             : 1.0;
                         return (Ordinal: kv.Key, Score: kv.Value * phraseBoost, Chunk: chunk);
                     })
                     .OrderByDescending(x => x.Score)
                     .ThenByDescending(x => x.Chunk.UpdatedUnix)
                     .ThenBy(x => x.Chunk.ChunkIndex)
                     .Take(Math.Clamp(topK, 1, 10)))
        {
            var chunk = pair.Chunk;
            hits.Add(new KnowledgeSearchHit(
                chunk.DocId,
                chunk.Title,
                chunk.ChunkIndex,
                chunk.Text,
                Math.Round(pair.Score, 4),
                chunk.UpdatedUnix));
        }

        return hits.ToArray();
    }

    private ScopeIndex BuildAndInsertLocked(string compositeScope, long revision, long generation, IReadOnlyList<KnowledgeChunkEntry> chunks)
    {
        if (_cache.Remove(compositeScope, out var old))
        {
            _cachedBytes = Math.Max(0, _cachedBytes - old.ApproximateBytes);
            _lru.Remove(old.LruNode);
        }

        var lengths = new int[chunks.Count];
        var postings = new Dictionary<string, List<(int Ordinal, int Freq)>>(StringComparer.Ordinal);
        long totalLen = 0;
        long approxBytes = chunks.Count * 64L;

        for (var i = 0; i < chunks.Count; i++)
        {
            approxBytes += (chunks[i].Title.Length + chunks[i].Text.Length) * 2L;
            var combined = string.IsNullOrEmpty(chunks[i].Title)
                ? chunks[i].Text
                : chunks[i].Title + " " + chunks[i].Text;
            var tokens = Tokenize(combined, includeSingleCjk: true);
            lengths[i] = Math.Max(1, tokens.Count);
            totalLen += lengths[i];

            if (tokens.Count == 0) continue;
            var tfMap = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var tok in tokens)
                tfMap[tok] = tfMap.TryGetValue(tok, out var c) ? c + 1 : 1;

            foreach (var (term, tf) in tfMap)
            {
                if (!postings.TryGetValue(term, out var list))
                {
                    list = new List<(int, int)>();
                    postings[term] = list;
                    approxBytes += 48 + term.Length * 2L;
                }
                list.Add((i, tf));
                approxBytes += 16;
            }
        }

        while (_lru.Last is { } tail
               && (_cache.Count >= MaxCachedScopes || _cachedBytes + approxBytes > MaxTotalCachedBytes))
        {
            _lru.RemoveLast();
            if (_cache.Remove(tail.Value, out var evicted))
                _cachedBytes = Math.Max(0, _cachedBytes - evicted.ApproximateBytes);
            if (!string.Equals(tail.Value, compositeScope, StringComparison.Ordinal))
                _generations.Remove(tail.Value);
        }

        var node = new LinkedListNode<string>(compositeScope);
        _lru.AddFirst(node);
        var built = new ScopeIndex
        {
            Revision = revision,
            Generation = generation,
            Chunks = chunks,
            DocLengths = lengths,
            AvgDocLength = chunks.Count == 0 ? 1.0 : (double)totalLen / chunks.Count,
            Postings = postings,
            LruNode = node,
            ApproximateBytes = approxBytes
        };
        _cache[compositeScope] = built;
        _cachedBytes += approxBytes;
        return built;
    }

    internal static List<string> Tokenize(string text, bool includeSingleCjk)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;

        var word = new StringBuilder();
        var cjkRun = new List<char>();

        void FlushWord()
        {
            if (word.Length > 0)
            {
                if (word.Length >= 2)
                    tokens.Add(word.ToString());
                else if (word.Length == 1 && char.IsDigit(word[0]))
                    tokens.Add(word.ToString());
                word.Clear();
            }
        }

        void FlushCjk()
        {
            if (cjkRun.Count == 0) return;
            if (cjkRun.Count == 1)
            {
                if (includeSingleCjk)
                    tokens.Add("u:" + cjkRun[0]);
            }
            else
            {
                for (var i = 0; i < cjkRun.Count - 1; i++)
                    tokens.Add(new string(new[] { cjkRun[i], cjkRun[i + 1] }));
                if (includeSingleCjk)
                {
                    for (var i = 0; i < cjkRun.Count; i++)
                        tokens.Add("u:" + cjkRun[i]);
                }
            }
            cjkRun.Clear();
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                FlushWord();
                cjkRun.Add(ch);
            }
            else if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or '-')
            {
                FlushCjk();
                if (word.Length < 48)
                    word.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                FlushWord();
                FlushCjk();
            }
        }

        FlushWord();
        FlushCjk();
        return tokens;
    }

    private static bool IsCjk(char ch) =>
        ch is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\uF900' and <= '\uFAFF')
            or (>= '\u3040' and <= '\u30FF')
            or (>= '\uAC00' and <= '\uD7AF');
}
