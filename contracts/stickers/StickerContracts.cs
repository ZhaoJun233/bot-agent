using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Stickers;

/// <summary>Result of validating a raw sticker binary payload (magic bytes &amp; size budget).</summary>
public readonly record struct StickerPayloadCheck(bool Valid, string Extension, string MimeType, string Error);

/// <summary>Credential-free snapshot of a stored sticker item passed across the host/package boundary.</summary>
public readonly record struct StickerItemSnapshot(
    string Id,
    string Ext,
    string Description,
    string[] Tags,
    bool Described,
    bool? IsSticker,
    int DescribeAttempts,
    int Uses,
    long AddedAt,
    long LastUsedAt);

/// <summary>Downloaded raw image bytes returned by the host image transport port.</summary>
public readonly record struct StickerDownloadedImage(byte[] Data, string Ext);

/// <summary>Model sticker description &amp; audit verdict returned by the host model port.</summary>
public readonly record struct StickerDescribeResult(string? Description, string[]? Tags, bool? IsSticker);

/// <summary>Model curation decision returned by the host model port.</summary>
public readonly record struct StickerCurateDecision(string[] WantedIds, string? Reason);

/// <summary>Credential-free live options snapshot consumed by <c>preset.feature.stickers</c>.</summary>
public readonly record struct StickerRuntimeOptions(
    bool EnableStickers,
    int StickerLibraryMax,
    int StickerCurateIntervalSeconds,
    int StickerCooldownSeconds);

/// <summary>
/// Narrow host port interface supplied to <c>preset.feature.stickers</c> via <see cref="PluginContext.Services" />.
/// Never exposes <c>SettingsBox</c>, raw database connections, or credentials.
/// </summary>
public interface IStickerHostPorts
{
    int StoreCount { get; }
    StickerItemSnapshot[] SnapshotStore();
    StickerItemSnapshot? FindInStore(string id);
    StickerItemSnapshot? AddToStore(byte[] data, string ext, string? fromUid, long fromGroup);
    string[] EnforceStoreLimit(int maxCount);
    Task<byte[]> ReadStickerBytesAsync(string id);
    void MarkDescribeFailed(string id);
    void SetDescription(string id, string? description, string[]? tags, bool? isSticker);
    bool RemoveFromStore(string id, string reason);
    Task<StickerDownloadedImage?> DownloadImageAsync(string url, long? messageId, CancellationToken ct);
    Task<StickerDescribeResult> DescribeStickerAsync(byte[] bytes, string mime, CancellationToken ct);
    Task<StickerCurateDecision> CurateStickersAsync(string table, int maxDelete, CancellationToken ct);
    Task<string[]> FetchCustomFacesAsync(int limit, CancellationToken ct);
    StickerRuntimeOptions GetOptions();
    bool IsPluginGateAllowed();
    DateTimeOffset UtcNow { get; }
    DateTimeOffset Now { get; }
    void Log(string message);
}

/// <summary>
/// Generation-bound sticker frequency gate, anti-repeat tracking, and payload safety policy
/// owned by <c>preset.feature.stickers</c>.
/// </summary>
public interface IStickerPolicyContribution : IPluginContribution
{
    bool Allow(string sourceKey, string stickerId, int cooldownSeconds, DateTimeOffset now, out string reason);

    string NoteSent(string sourceKey, string stickerId, DateTimeOffset now);

    bool TrySanitizeFilename(string? rawFilename, out string safeFilename);

    StickerPayloadCheck ValidatePayload(byte[] bytes, long maxBytes);

    void ClearConversation(string sourceKey);
}

/// <summary>
/// Generation-bound sticker runtime operations (collection, describe queue, curation timer, album import)
/// owned by <c>preset.feature.stickers</c>. Background timer lifetime is bound to package activation/shutdown.
/// </summary>
public interface IStickerRuntimeContribution : IPluginContribution
{
    int PendingDescribe { get; }
    long DescribeDone { get; }
    bool CanOperate { get; }
    void EnsureTimer(bool enabled);
    void QueueDescribe(string id);
    Task CollectAsync(string[] urls, string fromUid, long fromGroup, long messageId, Func<bool>? canStartExternalIo, CancellationToken ct);
    Task<string> CurateAsync(bool force, CancellationToken ct);
    Task<string> ImportFromAlbumAsync(int limit, CancellationToken ct);
}
