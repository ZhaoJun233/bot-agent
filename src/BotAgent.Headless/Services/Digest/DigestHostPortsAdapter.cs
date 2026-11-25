using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services.Conversations;
using BotAgent.Services.Digest;

namespace BotAgent.Services.Digest;

/// <summary>
/// 同会话纪要宿主端口实现（零新增 SQL 字面量）：
/// 合并当前会话实时内存窗口（即时感知尚未刷盘的撤回标记）与既有 <see cref="ConversationRegistry.ReadArchive"/>，
/// 严格绑定宿主传入的 <c>sourceKey</c>，并遵守 <c>EnableAgentMask</c> 昵称脱敏。
/// </summary>
public sealed class DigestHostPortsAdapter(
    ConversationRegistry registry,
    IModelClient brain,
    Func<AppSettings> settingsAccessor,
    Func<IReadOnlyDictionary<string, JsonElement>?> readSettings) : IDigestHostPorts
{
    private const string RecalledPrefix = "[已撤回]";

    public DigestOptions ReadOptions()
    {
        var values = readSettings();
        if (values is null) return new DigestOptions();
        var count = Math.Clamp(ReadInt(values, "defaultMessageCount", 80), 10, 200);
        var hours = Math.Clamp(ReadInt(values, "maxLookbackHours", 24), 1, 168);
        var cd = Math.Clamp(ReadInt(values, "cooldownSeconds", 20), 5, 300);
        return new DigestOptions(count, hours, cd);
    }

    public IReadOnlyList<DigestMessageItem> ReadConversationWindow(
        string sourceKey, long? sinceUnix, int maxMessages, string? focusKeyword)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
            return [];

        var settings = settingsAccessor();
        var mask = settings.AgentMaskSensitive;
        var limit = Math.Clamp(maxMessages, 1, 200);
        var bySeq = new Dictionary<long, (bool Recalled, DigestMessageItem Item)>();
        var syntheticArchiveSeq = 0L;

        // 1) 先读归档层（既有 ReadArchive，零新增 SQL）
        foreach (var arch in registry.ReadArchive(sourceKey, limit * 2))
        {
            var rawRole = (arch.Role ?? string.Empty).Trim();
            if (!string.Equals(rawRole, "peer", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rawRole, "user", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rawRole, "self", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rawRole, "assistant", StringComparison.OrdinalIgnoreCase))
                continue;

            var role = string.Equals(rawRole, "self", StringComparison.OrdinalIgnoreCase)
                || string.Equals(rawRole, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant" : "user";
            var label = role == "assistant"
                ? "Bot"
                : FormatSender(arch.SenderName, arch.SenderId, mask);
            var item = new DigestMessageItem(arch.Seq, role, label, (arch.Text ?? string.Empty).Trim(), arch.TimeUnix);
            var key = arch.Seq > 0 ? arch.Seq : -(++syntheticArchiveSeq);
            bySeq[key] = (arch.Recalled, item);
        }

        // 2) 再叠加内存活跃层（相同 Seq 以内存实时态为准，确保刚撤回尚未落库的消息 100% 被屏蔽）
        var live = registry.Find(sourceKey);
        if (live is not null)
        {
            var syntheticLiveSeq = 0L;
            foreach (var msg in live.Messages.ToList())
            {
                if (msg.Role is not (MessageRole.Peer or MessageRole.Self))
                    continue;
                var role = msg.Role == MessageRole.Self ? "assistant" : "user";
                var label = role == "assistant"
                    ? "Bot"
                    : FormatSender(msg.SenderName, msg.SenderId, mask);
                var unix = msg.Timestamp == default ? 0L : msg.Timestamp.ToUnixTimeSeconds();
                var item = new DigestMessageItem(msg.Seq, role, label, (msg.Text ?? string.Empty).Trim(), unix);
                var key = msg.Seq > 0 ? msg.Seq : -(++syntheticLiveSeq);
                bySeq[key] = (msg.Recalled, item);
            }
        }

        var focus = string.IsNullOrWhiteSpace(focusKeyword) ? null : focusKeyword.Trim();
        return bySeq.Values
            .Where(entry => !entry.Recalled
                && !string.IsNullOrWhiteSpace(entry.Item.Text)
                && !entry.Item.Text.StartsWith(RecalledPrefix, StringComparison.Ordinal)
                && (sinceUnix is null || entry.Item.TimestampUnix == 0 || entry.Item.TimestampUnix >= sinceUnix.Value)
                && (focus is null || entry.Item.Text.Contains(focus, StringComparison.OrdinalIgnoreCase)))
            .Select(entry => entry.Item)
            .OrderBy(item => item.Seq)
            .ThenBy(item => item.TimestampUnix)
            .TakeLast(limit)
            .ToArray();
    }

    public async Task<string?> CompleteDigestAsync(string systemPrompt, string transcriptBlock, CancellationToken ct)
    {
        var model = settingsAccessor().Model;
        return await brain.CompleteChatAsync(
            model,
            systemPrompt,
            [("user", transcriptBlock)],
            maxTokens: 1200,
            temperature: 0.2,
            ct: ct).ConfigureAwait(false);
    }

    private static string FormatSender(string? name, long? uid, bool mask)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return mask ? Services.Agent.AgentMask.Text(name.Trim(), [name.Trim()]) : name.Trim();
        if (uid is { } id && id > 0)
            return mask ? Services.Agent.AgentMask.Digits(id.ToString()) : id.ToString();
        return "成员";
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }
}
