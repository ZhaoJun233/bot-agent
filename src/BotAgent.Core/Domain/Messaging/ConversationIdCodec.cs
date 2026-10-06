using System.Diagnostics.CodeAnalysis;
using System.Text;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Messaging;

/// <summary>
/// 会话 key 编码与解码器。集中处理 v=1 结构化编码与旧 QQ key 兼容解析。
/// </summary>
public static class ConversationIdCodec
{
    public const int MaxKeyLength = 512;
    private const string VersionPrefix = "v=1;";

    public static string Encode(ConversationId id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (string.Equals(id.AccountScope, AccountScope.Legacy, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(id.ThreadId))
        {
            var isGroup = id.Kind == ConversationKind.GroupChat;
            var kindTag = isGroup ? "group:" : "private:";
            var normPlatform = PlatformId.Normalize(id.PlatformId);
            return normPlatform switch
            {
                PlatformId.QqPrivate => kindTag + id.NativeTargetId,
                PlatformId.QqOfficial => "official:" + kindTag + id.NativeTargetId,
                PlatformId.Local => "local:" + kindTag + id.NativeTargetId,
                PlatformId.Feishu => "feishu:" + kindTag + id.NativeTargetId,
                _ => EncodeStructured(id),
            };
        }

        return EncodeStructured(id);
    }

    public static string EncodeStructured(ConversationId id)
    {
        ArgumentNullException.ThrowIfNull(id);

        var kind = id.Kind switch
        {
            ConversationKind.GroupChat => "group",
            ConversationKind.PrivateChat => "direct",
            ConversationKind.ChannelChat => "channel",
            _ => "local",
        };

        var sb = new StringBuilder(128);
        sb.Append("v=1;platform=").Append(Uri.EscapeDataString(id.PlatformId))
          .Append(";account=").Append(Uri.EscapeDataString(id.AccountScope))
          .Append(";kind=").Append(kind)
          .Append(";target=").Append(Uri.EscapeDataString(id.NativeTargetId))
          .Append(";thread=").Append(Uri.EscapeDataString(id.ThreadId ?? string.Empty));

        return sb.ToString();
    }

    public static bool TryParse(string? sourceKey, [NotNullWhen(true)] out ConversationId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > MaxKeyLength)
        {
            return false;
        }

        if (sourceKey.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return false;
        }

        if (sourceKey.StartsWith(VersionPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return TryParseStructured(sourceKey, out id);
        }

        return TryParseLegacy(sourceKey, out id);
    }

    public static ConversationId? ParseOrDefault(string? sourceKey)
        => TryParse(sourceKey, out var id) ? id : null;

    private static bool TryParseStructured(string key, [NotNullWhen(true)] out ConversationId? id)
    {
        id = null;
        var parts = key.Split(';');
        string? platform = null;
        string? account = null;
        string? kindStr = null;
        string? target = null;
        string? thread = null;

        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var k = part[..eq].Trim();
            var rawVal = eq + 1 < part.Length ? part[(eq + 1)..].Trim() : string.Empty;
            string val;
            try
            {
                val = Uri.UnescapeDataString(rawVal);
            }
            catch (UriFormatException)
            {
                return false;
            }

            switch (k.ToLowerInvariant())
            {
                case "platform": platform = val; break;
                case "account": account = val; break;
                case "kind": kindStr = val; break;
                case "target": target = val; break;
                case "thread": thread = string.IsNullOrEmpty(val) ? null : val; break;
            }
        }

        if (string.IsNullOrWhiteSpace(platform)
            || string.IsNullOrWhiteSpace(target)
            || string.IsNullOrWhiteSpace(kindStr))
        {
            return false;
        }

        ConversationKind kind;
        switch (kindStr.ToLowerInvariant())
        {
            case "group": kind = ConversationKind.GroupChat; break;
            case "direct":
            case "private": kind = ConversationKind.PrivateChat; break;
            case "channel": kind = ConversationKind.ChannelChat; break;
            case "local": kind = ConversationKind.LocalTest; break;
            default: return false;
        }

        id = new ConversationId(
            PlatformId: PlatformId.Normalize(platform),
            AccountScope: string.IsNullOrWhiteSpace(account) ? AccountScope.Default : account,
            Kind: kind,
            NativeTargetId: target,
            ThreadId: thread);
        return true;
    }

    private static bool TryParseLegacy(string key, [NotNullWhen(true)] out ConversationId? id)
    {
        id = null;
        string prefix;
        var work = key;

        if (work.StartsWith("official:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = PlatformId.QqOfficial;
            work = work["official:".Length..];
        }
        else if (work.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = PlatformId.Local;
            work = work["local:".Length..];
        }
        else if (work.StartsWith("feishu:", StringComparison.OrdinalIgnoreCase))
        {
            prefix = PlatformId.Feishu;
            work = work["feishu:".Length..];
        }
        else
        {
            prefix = PlatformId.QqPrivate;
        }

        var colon = work.IndexOf(':');
        if (colon <= 0 || colon >= work.Length - 1)
        {
            return false;
        }

        var tag = work[..colon].ToLowerInvariant();
        var targetId = work[(colon + 1)..].Trim();
        if (string.IsNullOrEmpty(targetId) || targetId.IndexOfAny([':', ';', ' ']) >= 0)
        {
            return false;
        }

        ConversationKind kind;
        switch (tag)
        {
            case "group": kind = ConversationKind.GroupChat; break;
            case "private": kind = ConversationKind.PrivateChat; break;
            default: return false;
        }

        id = new ConversationId(
            PlatformId: prefix,
            AccountScope: AccountScope.Legacy,
            Kind: kind,
            NativeTargetId: targetId,
            ThreadId: null);
        return true;
    }
}
