using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed partial class FeishuBotGateway
{
    private const int MaxCachedGroups = 512;
    private const int MaxCachedUsers = 2048;
    private const int MaxCachedThreads = 1024;
    private const int MaxImageResourceBytes = 6 * 1024 * 1024;
    private const int MaxAudioResourceBytes = 8 * 1024 * 1024;
    private const int MaxFileResourceBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan MetadataCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(10);

    private sealed record CachedLabel(string? Value, DateTimeOffset ExpiresAt);
    private readonly ConcurrentDictionary<string, CachedLabel> _groupNameCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedLabel> _userNameCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _threadByMessageId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _rootByThreadId = new(StringComparer.Ordinal);

    private void RememberThreadTopology(string messageId, string? rootId, string? threadId)
    {
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(threadId))
            return;
        var tid = threadId.Trim();
        TrimDictIfNeeded(_threadByMessageId, MaxCachedThreads);
        _threadByMessageId[messageId] = tid;
        var anchor = !string.IsNullOrWhiteSpace(rootId) ? rootId.Trim() : messageId;
        TrimDictIfNeeded(_rootByThreadId, MaxCachedThreads);
        _rootByThreadId.TryAdd(tid, anchor);
    }

    private string ResolveCachedSenderName(string appId, string senderOpenId)
    {
        var cacheKey = $"{appId}:{senderOpenId}";
        if (_userNameCache.TryGetValue(cacheKey, out var hit) && Clock.UtcNow < hit.ExpiresAt && !string.IsNullOrWhiteSpace(hit.Value))
            return hit.Value!;
        var suffix = senderOpenId.Length > 4 ? senderOpenId[^4..] : senderOpenId;
        return $"飞书用户#{suffix}";
    }

    private void TryWarmSenderNameBackground(FeishuSettingsSnapshot settings, string senderOpenId)
    {
        if (!IsSafeFeishuToken(senderOpenId))
            return;

        var cacheKey = $"{settings.AppId}:{senderOpenId}";
        if (_userNameCache.TryGetValue(cacheKey, out var hit) && Clock.UtcNow < hit.ExpiresAt)
            return;

        var cached = Volatile.Read(ref _tokenCache);
        if (cached is null || cached.Settings != settings || Clock.Now >= cached.ExpiresAt || string.IsNullOrWhiteSpace(cached.Token))
            return;
        var token = cached.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                using var op = _operations.TryEnter(CancellationToken.None);
                if (op is null || _disposed) return;
                using var req = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{settings.ApiBase}/open-apis/contact/v3/users/{Uri.EscapeDataString(senderOpenId)}?user_id_type=open_id");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                using var resp = await _http.SendAsync(req, op.Token).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(op.Token).ConfigureAwait(false);
                var root = JsonNode.Parse(body) as JsonObject;
                var code = root?["code"]?.GetValue<int>() ?? -1;
                var name = (root?["data"]?["user"] as JsonObject)?["name"]?.GetValue<string>()?.Trim();
                TrimDictIfNeeded(_userNameCache, MaxCachedUsers);
                if (resp.IsSuccessStatusCode && code == 0 && !string.IsNullOrWhiteSpace(name))
                {
                    _userNameCache[cacheKey] = new CachedLabel(name, Clock.UtcNow.Add(MetadataCacheTtl));
                }
                else
                {
                    var suffix = senderOpenId.Length > 4 ? senderOpenId[^4..] : senderOpenId;
                    _userNameCache[cacheKey] = new CachedLabel($"飞书用户#{suffix}", Clock.UtcNow.Add(NegativeCacheTtl));
                }
            }
            catch { }
        });
    }

    public async Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
    {
        using var op = _operations.TryEnter(ct);
        if (_disposed || op is null || groupId <= 0) return null;
        var settings = Snapshot(_box.Current);
        var chatId = _ids.OriginalOf(groupId, settings.AppId, "group");
        if (!IsSafeFeishuToken(chatId)) return null;

        var cacheKey = $"{settings.AppId}:{chatId}";
        if (_groupNameCache.TryGetValue(cacheKey, out var cached) && Clock.UtcNow < cached.ExpiresAt)
            return cached.Value;

        try
        {
            var token = await GetTenantTokenAsync(settings, op.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{settings.ApiBase}/open-apis/im/v1/chats/{Uri.EscapeDataString(chatId!)}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await _http.SendAsync(req, op.Token).ConfigureAwait(false);
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(op.Token).ConfigureAwait(false)) as JsonObject;
            var code = root?["code"]?.GetValue<int>() ?? -1;
            var name = (root?["data"] as JsonObject)?["name"]?.GetValue<string>()?.Trim();

            TrimDictIfNeeded(_groupNameCache, MaxCachedGroups);
            if (resp.IsSuccessStatusCode && code == 0 && !string.IsNullOrWhiteSpace(name))
            {
                _groupNameCache[cacheKey] = new CachedLabel(name, Clock.UtcNow.Add(MetadataCacheTtl));
                return name;
            }

            _groupNameCache[cacheKey] = new CachedLabel(null, Clock.UtcNow.Add(NegativeCacheTtl));
            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<(string? Text, string? SenderId)> GetQuotedMessageAsync(MessageRef messageRef, CancellationToken ct = default)
    {
        using var op = _operations.TryEnter(ct);
        if (_disposed || op is null || messageRef is null || !IsSafeFeishuToken(messageRef.NativeMessageId))
            return (null, null);

        if (!string.Equals(PlatformId.Normalize(messageRef.Conversation.PlatformId), PlatformId.Feishu, StringComparison.Ordinal)
            || !string.Equals(messageRef.Conversation.AccountScope, Context.AccountScope, StringComparison.Ordinal))
            return (null, null);

        var settings = Snapshot(_box.Current);
        if (_ids.TryGetAlias(settings.AppId, "message", messageRef.NativeMessageId, out var alias)
            && alias > 0
            && _outbox.LastOrDefault(o => o.MessageId == alias) is { } own)
            return (own.Text, null);

        return await FetchMessageByNativeIdAsync(settings, messageRef.NativeMessageId, op.Token).ConfigureAwait(false);
    }

    public async Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
    {
        using var op = _operations.TryEnter(ct);
        if (_disposed || op is null || messageId <= 0) return (null, 0);
        if (_outbox.LastOrDefault(o => o.MessageId == messageId) is { } own)
            return (own.Text, 0);

        var settings = Snapshot(_box.Current);
        var nativeId = _ids.OriginalOf(messageId, settings.AppId, "message");
        if (!IsSafeFeishuToken(nativeId)) return (null, 0);

        var (text, senderNative) = await FetchMessageByNativeIdAsync(settings, nativeId!, op.Token).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return (null, 0);
        var senderAlias = !string.IsNullOrWhiteSpace(senderNative)
            ? _ids.AliasFor(settings.AppId, "participant", senderNative)
            : 0;
        return (text, senderAlias);
    }

    private async Task<(string? Text, string? SenderId)> FetchMessageByNativeIdAsync(
        FeishuSettingsSnapshot settings,
        string nativeMessageId,
        CancellationToken ct)
    {
        if (!IsSafeFeishuToken(nativeMessageId)) return (null, null);
        try
        {
            var token = await GetTenantTokenAsync(settings, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return (null, null);

            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"{settings.ApiBase}/open-apis/im/v1/messages/{Uri.EscapeDataString(nativeMessageId)}");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, null);

            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            if ((root?["code"]?.GetValue<int>() ?? -1) != 0) return (null, null);

            var item = (root?["data"]?["items"] as JsonArray)?.FirstOrDefault() as JsonObject
                ?? root?["data"]?["message"] as JsonObject;
            if (item is null) return (null, null);

            var msgType = item["msg_type"]?.GetValue<string>() ?? item["message_type"]?.GetValue<string>();
            var contentJson = (item["body"] as JsonObject)?["content"]?.GetValue<string>()
                ?? item["content"]?.GetValue<string>() ?? string.Empty;
            var senderId = (item["sender"] as JsonObject)?["id"]?.GetValue<string>()?.Trim();
            var text = FeishuInboundRichParser.ParseQuotedSummary(msgType, contentJson);
            return (string.IsNullOrWhiteSpace(text) ? null : text, senderId);
        }
        catch
        {
            return (null, null);
        }
    }

    public async Task<byte[]?> DownloadMediaUriAsync(string mediaUri, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(mediaUri) || !mediaUri.StartsWith("feishu-media://", StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = mediaUri["feishu-media://".Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash >= rest.Length - 1) return null;
        var msgId = rest[..slash];
        var fileKey = rest[(slash + 1)..];
        return await DownloadMessageResourceAsync(msgId, fileKey, "image", MaxImageResourceBytes, ct).ConfigureAwait(false);
    }

    public async Task<(byte[]? Data, string? RemoteUrl, string FileName)> ResolveInboundVoiceAsync(
        InboundVoiceAttachment voice,
        long messageId,
        CancellationToken ct = default)
    {
        if (TrySplitMessageFileRef(voice.PlatformAudioRef, out var rawMid, out var fileKey))
        {
            var data = await DownloadMessageResourceAsync(rawMid, fileKey, "file", MaxAudioResourceBytes, ct).ConfigureAwait(false);
            return (data, null, "feishu_voice.ogg");
        }
        return (null, voice.WavUrl, "voice.wav");
    }

    public async Task<(byte[]? Data, string? RemoteUrl, string FileName)> ResolveInboundFileAsync(
        InboundFileAttachment file,
        bool isGroup,
        long targetId,
        long messageId,
        CancellationToken ct = default)
    {
        var safeName = string.IsNullOrWhiteSpace(file.FileName) ? "attachment.dat" : file.FileName;
        if (TrySplitMessageFileRef(file.PlatformFileId, out var rawMid, out var fileKey))
        {
            var data = await DownloadMessageResourceAsync(rawMid, fileKey, "file", MaxFileResourceBytes, ct).ConfigureAwait(false);
            return (data, null, safeName);
        }
        return (null, file.DownloadUrl, safeName);
    }

    private async Task<byte[]?> DownloadMessageResourceAsync(
        string nativeMessageId,
        string fileKey,
        string resourceType,
        int maxBytes,
        CancellationToken ct)
    {
        if (!IsSafeFeishuToken(nativeMessageId) || !IsSafeFeishuToken(fileKey))
            return null;

        using var op = _operations.TryEnter(ct);
        if (_disposed || op is null) return null;

        try
        {
            var settings = Snapshot(_box.Current);
            var token = await GetTenantTokenAsync(settings, op.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token)) return null;

            var url = $"{settings.ApiBase}/open-apis/im/v1/messages/{Uri.EscapeDataString(nativeMessageId)}/resources/{Uri.EscapeDataString(fileKey)}?type={Uri.EscapeDataString(resourceType)}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, op.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is { } len && (len <= 0 || len > maxBytes))
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync(op.Token).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buf = new byte[8192];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), op.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes) return null;
                ms.Write(buf, 0, read);
            }
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSafeFeishuToken(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var trimmed = id.Trim();
        if (trimmed.Length > 128 || trimmed is "." or "..") return false;
        foreach (var ch in trimmed)
        {
            if (!(char.IsLetterOrDigit(ch) || ch is '_' or '-'))
                return false;
        }
        return true;
    }

    private static bool TrySplitMessageFileRef(string? rawRef, out string messageId, out string fileKey)
    {
        messageId = string.Empty;
        fileKey = string.Empty;
        if (string.IsNullOrWhiteSpace(rawRef)) return false;
        var sep = rawRef.IndexOf('|');
        if (sep <= 0 || sep >= rawRef.Length - 1) return false;
        messageId = rawRef[..sep].Trim();
        fileKey = rawRef[(sep + 1)..].Trim();
        return IsSafeFeishuToken(messageId) && IsSafeFeishuToken(fileKey);
    }

    private static void TrimDictIfNeeded<TValue>(ConcurrentDictionary<string, TValue> dict, int max)
    {
        if (dict.Count < max) return;
        foreach (var key in dict.Keys.Take(max / 8 + 1))
            dict.TryRemove(key, out _);
    }
}
