using System.Collections.Concurrent;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Stickers;

namespace BotAgent.Packages.Stickers;

/// <summary>
/// Owns sticker cooldown / same-sticker anti-repeat gating and magic-number payload validation.
/// </summary>
public sealed class StickerPolicyEngine : IStickerPolicyContribution
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string Id)> _lastSent = new(StringComparer.Ordinal);

    public bool Allow(string sourceKey, string stickerId, int cooldownSeconds, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        var cooldown = Math.Max(0, cooldownSeconds);
        if (!_lastSent.TryGetValue(sourceKey, out var last))
        {
            return true;
        }

        var since = now - last.At;
        if (cooldown > 0 && since.TotalSeconds < cooldown)
        {
            reason = $"距上次发表情包只有 {since.TotalSeconds:F0} 秒（冷却 {cooldown} 秒）";
            return false;
        }

        if (string.Equals(last.Id, stickerId, StringComparison.OrdinalIgnoreCase) && since.TotalMinutes < 10)
        {
            reason = $"这张 {stickerId} 十分钟内已经发过";
            return false;
        }

        return true;
    }

    public string NoteSent(string sourceKey, string stickerId, DateTimeOffset now)
    {
        var suffix = _lastSent.TryGetValue(sourceKey, out var prev)
            ? $"（距上次发表情包 {(now - prev.At).TotalSeconds:F0} 秒）"
            : string.Empty;
        _lastSent[sourceKey] = (now, stickerId);
        return suffix;
    }

    public bool TrySanitizeFilename(string? rawFilename, out string safeFilename)
        => StickerSafetyGuard.TrySanitizeFilename(rawFilename, out safeFilename);

    public StickerPayloadCheck ValidatePayload(byte[] bytes, long maxBytes)
    {
        var ok = StickerSafetyGuard.ValidatePayload(bytes.AsSpan(), maxBytes, out var ext, out var mime, out var err);
        return new StickerPayloadCheck(ok, ext, mime, err);
    }

    public void ClearConversation(string sourceKey)
        => _lastSent.TryRemove(sourceKey, out _);
}
