using System.Text.Json;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;

namespace BotAgent.Adapters.Persistence;

/// <summary>Versioned Feishu identities; never imports or infers legacy native bindings.</summary>
public sealed class FeishuIdMap
{
    public const long AliasBase = Channels.FeishuBase + 1_000_000_000_000L;
    public const long AliasLimit = Channels.FeishuBase + 2_000_000_000_000L;
    private readonly OfficialIdMap _ids;

    public FeishuIdMap(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _ids = new OfficialIdMap(path, AliasBase, AliasLimit, durable: true, validateOriginal: IsIdentity);
    }

    // No implicit AppPaths access: existing pure probes retain an explicit ephemeral bridge.
    public FeishuIdMap()
        => _ids = new OfficialIdMap(null, AliasBase, AliasLimit, durable: true, validateOriginal: IsIdentity);

    public long AliasFor(string appId, string kind, string nativeId)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(nativeId)) return 0;
        var key = JsonSerializer.Serialize(new[] { "v2", PlatformId.Feishu, appId, kind, nativeId });
        try { return _ids.AliasFor(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 0;
        }
    }

    public string? OriginalOf(long alias, string appId, string kind)
    {
        var key = _ids.OriginalOf(alias);
        if (key is null) return null;
        var parts = JsonSerializer.Deserialize<string[]>(key);
        return parts is { Length: 5 } && parts[0] == "v2" && parts[1] == PlatformId.Feishu
            && parts[2] == appId && parts[3] == kind ? parts[4] : null;
    }

    private static bool IsIdentity(string key)
    {
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(key);
            return parts is { Length: 5 } && parts[0] == "v2" && parts[1] == PlatformId.Feishu
                && !string.IsNullOrWhiteSpace(parts[2]) && parts[3] is "group" or "participant" or "message"
                && !string.IsNullOrWhiteSpace(parts[4]);
        }
        catch (JsonException) { return false; }
    }
}
