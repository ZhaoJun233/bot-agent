using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Knowledge;

/// <summary>Declarative runtime options for <c>preset.feature.knowledge</c>.</summary>
public sealed record KnowledgeOptions(
    int MaxDocsPerScope = 256,
    int MaxChunksPerScope = 4096,
    int MaxChunkChars = 800,
    int DefaultTopK = 4);

/// <summary>Metadata summary of a document stored in a single conversation knowledge scope.</summary>
public sealed record KnowledgeDocumentItem(
    string DocId,
    string Title,
    string SourceKind,
    int ChunkCount,
    int TotalChars,
    long UpdatedUnix);

/// <summary>Ranked BM25 retrieval hit scoped strictly to the requesting conversation.</summary>
public sealed record KnowledgeSearchHit(
    string DocId,
    string Title,
    int ChunkIndex,
    string Snippet,
    double Score,
    long UpdatedUnix);

/// <summary>Outcome returned when saving text or an attachment into conversation knowledge.</summary>
public sealed record KnowledgeSaveResult(
    bool Ok,
    string ReasonCode,
    KnowledgeDocumentItem? Document = null);

/// <summary>
/// Pure storage abstraction implemented by <c>BotAgent.Storage.KnowledgeStore</c> so
/// <c>BotAgent.Headless.Services</c> has zero references to storage/adapter concrete types (R9).
/// </summary>
public interface IKnowledgeStore
{
    KnowledgeSaveResult SaveDocument(string compositeScope, string title, string content, string sourceKind, KnowledgeOptions options);

    KnowledgeSearchHit[] Search(string compositeScope, string query, int topK);

    KnowledgeDocumentItem[] ListDocuments(string compositeScope);

    bool ForgetDocument(string compositeScope, string docId);

    void ClearScope(string compositeScope);
}

/// <summary>
/// Host-owned narrow port bound to the live conversation's composite scope.
/// Package code never inspects cross-conversation keys or performs direct SQL/network IO.
/// </summary>
public interface IKnowledgeHostPorts
{
    KnowledgeOptions ReadOptions();

    KnowledgeSaveResult SaveText(string sourceKey, string title, string content, string sourceKind);

    Task<KnowledgeSaveResult> SaveAttachmentAsync(string sourceKey, string? attachmentSelector, string? titleOverride, CancellationToken ct);

    KnowledgeSearchHit[] Search(string sourceKey, string query, int topK);

    KnowledgeDocumentItem[] List(string sourceKey);

    bool Forget(string sourceKey, string docId);

    void ClearConversation(string sourceKey);
}

/// <summary>Package contribution exposed by <c>preset.feature.knowledge</c>.</summary>
public interface IKnowledgeContribution : IPluginContribution
{
    bool IsReady { get; }

    KnowledgeSaveResult SaveText(string sourceKey, string title, string content, string sourceKind);

    Task<KnowledgeSaveResult> SaveAttachmentAsync(string sourceKey, string? attachmentSelector, string? titleOverride, CancellationToken ct);

    KnowledgeSearchHit[] Search(string sourceKey, string query, int topK);

    KnowledgeDocumentItem[] List(string sourceKey);

    bool Forget(string sourceKey, string docId);

    void ClearConversation(string sourceKey);
}
