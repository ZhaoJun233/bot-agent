using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Music;

/// <summary>Structured outcome for music card or fallback text sends across the package boundary.</summary>
public sealed record MusicSendOutcome(
    bool Ok,
    bool HasDeliveryReceipt,
    bool DeliverySucceeded,
    string? DeliveryStatus,
    string? ErrorMessage,
    string? PlatformMessageId,
    long MessageId = 0,
    string? ReceiptToken = null);

/// <summary>
/// Narrow, credential-free host ports provided to <c>preset.feature.music</c> for media resolution and platform sends.
/// </summary>
public interface IMusicHostPorts
{
    bool IsMediaReady { get; }

    bool IsSourceConnected { get; }

    Task<string?> DescribeByNameAsync(string keyword, string senderName, CancellationToken ct);

    Task<string?> DescribeShareAsync(
        string platform,
        string? songId,
        string? title,
        string? artist,
        string? pageUrl,
        string? directAudioUrl,
        string senderName,
        CancellationToken ct);

    Task<string?> ResolveSongIdByNameAsync(string keyword, CancellationToken ct);

    Task<MusicSendOutcome> SendMusicAsync(bool isGroup, long targetId, string songId, string title, CancellationToken ct);

    Task<MusicSendOutcome> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct);

    void Log(string message);
}

/// <summary>
/// Generation-bound music listening cooldown, one-shot listening notes, and share deduplication
/// owned by <c>preset.feature.music</c>.
/// </summary>
public interface IMusicPolicyContribution : IPluginContribution
{
    bool TryBeginListen(string sourceKey, int cooldownSeconds, DateTimeOffset now, out string reason);

    void AddNote(string sourceKey, string note);

    bool HasNote(string sourceKey);

    string? TakeNote(string sourceKey);

    bool TryRecordShare(string sourceKey, string songId, TimeSpan dedupWindow, DateTimeOffset now);

    void ClearConversation(string sourceKey);
}

/// <summary>
/// Full music runtime coordinator owned by <c>preset.feature.music</c>: listening orchestration,
/// panel self-test, share resolution with legacy fallback, cooldowns, and one-shot listening notes.
/// </summary>
public interface IMusicRuntimeContribution : IMusicPolicyContribution
{
    string? LastTestHeader { get; }

    Task<string?> TestByNameAsync(string song, CancellationToken ct);

    Task<string?> DescribeShareAsync(
        string platform,
        string? songId,
        string? title,
        string? artist,
        string? pageUrl,
        string? directAudioUrl,
        string sender,
        CancellationToken ct);

    Task<string?> ListenAsync(string sourceKey, string song, string requester, CancellationToken ct);

    Task<MusicSendOutcome> ShareWithReceiptAsync(string song, bool isGroup, long targetId, CancellationToken ct);
}
