namespace BotAgent.Domain.Ports;

public sealed record WorkspaceMessageItem(
    long Sequence,
    string Role,
    string Text,
    string Mode,
    string? AttachmentName,
    long CreatedUnix,
    string? TaskId = null,
    string? Status = null);

public sealed record WorkspaceMessagesSnapshot(
    long LastSequence,
    IReadOnlyList<WorkspaceMessageItem> Messages);

/// <summary>Pure storage port strictly scoped to <c>workspace:owner</c>; never exposes QQ/Feishu conversations.</summary>
public interface IWorkspaceConversationStore
{
    WorkspaceMessagesSnapshot ReadAfter(long afterSequence, int limit = 50);

    WorkspaceMessageItem Append(
        string role,
        string text,
        string mode,
        long nowUnix,
        string? idempotencyKey = null,
        string? attachmentName = null,
        string? taskId = null,
        string? status = null,
        bool allowIdempotentReplay = true);

    bool TryFindByIdempotencyKey(string idempotencyKey, out WorkspaceMessageItem? item);

    void Clear(long nowUnix);
}
