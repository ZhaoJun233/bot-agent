using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Platforms;

namespace BotAgent.Adapters.Platforms.Feishu;

public sealed partial class FeishuBotGateway
{
    private sealed record FeishuBotInfoCache(FeishuSettingsSnapshot Settings, string BotOpenId, DateTimeOffset ExpiresAt);
    private FeishuBotInfoCache? _botInfoCache;
    private readonly SemaphoreSlim _botInfoGate = new(1, 1);

    internal static bool TryDecryptCipherPayload(string cipherBase64, string encryptKey, out JsonObject? decrypted)
    {
        decrypted = null;
        if (string.IsNullOrWhiteSpace(cipherBase64) || string.IsNullOrWhiteSpace(encryptKey))
            return false;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(cipherBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (raw.Length < 32 || raw.Length % 16 != 0)
            return false;

        try
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptKey));
            using var aes = Aes.Create();
            aes.Key = key;
            var plainBytes = aes.DecryptCbc(raw.AsSpan(16), raw.AsSpan(0, 16), PaddingMode.PKCS7);
            var json = Encoding.UTF8.GetString(plainBytes);
            decrypted = JsonNode.Parse(json) as JsonObject;
            return decrypted is not null;
        }
        catch
        {
            return false;
        }
    }

    private (bool Handled, int StatusCode, string ResponseBody)? TryUnwrapEncryptedEnvelope(
        string rawBody,
        JsonObject outerPayload,
        PlatformOptions settings,
        string? signature,
        string? timestamp,
        string? nonce,
        out JsonObject activePayload,
        out bool decryptedWithEncryptKey,
        out bool unsignedEncryptedMode)
    {
        activePayload = outerPayload;
        decryptedWithEncryptKey = false;
        unsignedEncryptedMode = false;
        var encryptKey = settings.FeishuEncryptKey;
        if (!outerPayload.TryGetPropertyValue("encrypt", out var encryptNode) || encryptNode is null)
        {
            if (!string.IsNullOrWhiteSpace(encryptKey) && !VerifySignature(rawBody, signature, timestamp, nonce, encryptKey))
            {
                LastErrorCode = string.IsNullOrWhiteSpace(signature) ? "encrypted_payload_required" : "signature_mismatch";
                return (false, 401, $"{{\"error\":\"{LastErrorCode}\"}}");
            }
            return null;
        }

        if (encryptNode is not JsonValue val || !val.TryGetValue<string>(out var cipherText) || string.IsNullOrWhiteSpace(cipherText))
        {
            LastErrorCode = "invalid_payload";
            return (false, 400, "{\"error\":\"invalid_payload\"}");
        }

        if (string.IsNullOrWhiteSpace(encryptKey))
        {
            LastErrorCode = "encrypt_key_not_configured";
            return (false, 403, "{\"error\":\"encrypt_key_not_configured\"}");
        }

        if (!string.IsNullOrWhiteSpace(signature))
        {
            if (!VerifySignature(rawBody, signature, timestamp, nonce, encryptKey))
            {
                LastErrorCode = "signature_mismatch";
                return (false, 401, "{\"error\":\"signature_mismatch\"}");
            }
            if (!TryDecryptCipherPayload(cipherText, encryptKey, out var verifiedInner) || verifiedInner is null)
            {
                LastErrorCode = "decrypt_failed";
                return (false, 401, "{\"error\":\"decrypt_failed\"}");
            }
            activePayload = verifiedInner;
            decryptedWithEncryptKey = true;
            return null;
        }

        // 无签名密文请求只允许合法的 url_verification，所有失败分支统一返回相同 401 以消除 Padding/JSON 预言机。
        unsignedEncryptedMode = true;
        if (!TryDecryptCipherPayload(cipherText, encryptKey, out var inner) || inner is null)
        {
            LastErrorCode = "unauthorized";
            return (false, 401, "{\"error\":\"unauthorized\"}");
        }

        activePayload = inner;
        decryptedWithEncryptKey = true;
        return null;
    }

    private (bool Handled, int StatusCode, string ResponseBody)? TryHandleUrlVerification(
        PlatformOptions settings,
        string rawBody,
        string? type,
        string? challenge,
        string? suppliedVerificationToken,
        bool decryptedWithEncryptKey,
        bool unsignedEncryptedMode,
        string? signature,
        string? timestamp,
        string? nonce,
        CancellationToken ct)
    {
        if (!string.Equals(type, "url_verification", StringComparison.OrdinalIgnoreCase))
        {
            if (unsignedEncryptedMode)
            {
                LastErrorCode = "unauthorized";
                return (false, 401, "{\"error\":\"unauthorized\"}");
            }
            return null;
        }

        var configuredVerificationToken = settings.FeishuVerificationToken?.Trim() ?? string.Empty;
        var hasValidToken = !string.IsNullOrWhiteSpace(configuredVerificationToken)
            && string.Equals(suppliedVerificationToken, configuredVerificationToken, StringComparison.Ordinal)
            && (string.IsNullOrWhiteSpace(settings.FeishuEncryptKey)
                || decryptedWithEncryptKey
                || VerifySignature(rawBody, signature, timestamp, nonce, settings.FeishuEncryptKey));
        var hasValidEncryptAuth = string.IsNullOrWhiteSpace(configuredVerificationToken)
            && !string.IsNullOrWhiteSpace(settings.FeishuEncryptKey)
            && (decryptedWithEncryptKey || VerifySignature(rawBody, signature, timestamp, nonce, settings.FeishuEncryptKey));

        if (!hasValidToken && !hasValidEncryptAuth)
        {
            if (unsignedEncryptedMode)
            {
                LastErrorCode = "unauthorized";
                return (false, 401, "{\"error\":\"unauthorized\"}");
            }
            LastErrorCode = "verification_token_mismatch";
            return (false, 403, "{\"error\":\"verification_token_mismatch\"}");
        }

        if (string.IsNullOrWhiteSpace(challenge))
        {
            return unsignedEncryptedMode
                ? (false, 401, "{\"error\":\"unauthorized\"}")
                : (false, 400, "{\"error\":\"missing_challenge\"}");
        }

        ct.ThrowIfCancellationRequested();
        var escapedChallenge = JsonValue.Create(challenge)?.ToJsonString() ?? "\"\"";
        return (true, 200, $"{{\"challenge\":{escapedChallenge}}}");
    }

    private async Task<string> GetTenantTokenAsync(FeishuSettingsSnapshot settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(settings.AppId) || string.IsNullOrWhiteSpace(settings.AppSecret))
            return string.Empty;

        var cached = Volatile.Read(ref _tokenCache);
        if (cached is not null && cached.Settings == settings && Clock.Now < cached.ExpiresAt)
            return cached.Token;

        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _tokenCache);
            if (cached is not null && cached.Settings == settings && Clock.Now < cached.ExpiresAt)
                return cached.Token;

            var url = $"{settings.ApiBase}/open-apis/auth/v3/tenant_access_token/internal";
            var payload = new JsonObject
            {
                ["app_id"] = settings.AppId,
                ["app_secret"] = settings.AppSecret,
            };

            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return string.Empty;

            JsonObject? json;
            try
            {
                json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            }
            catch (JsonException)
            {
                LastErrorCode = "feishu_token_invalid";
                return string.Empty;
            }

            if (json is null
                || !TryGetOptionalString(json, "tenant_access_token", out var token)
                || string.IsNullOrWhiteSpace(token)
                || !TryGetOptionalInt(json, "expire", out var expire))
            {
                LastErrorCode = "feishu_token_invalid";
                return string.Empty;
            }

            Volatile.Write(ref _tokenCache, new FeishuTokenCache(settings, token, Clock.Now.AddSeconds(Math.Max(60, (expire ?? 7200) - 300))));
            ConnectionChanged?.Invoke(IsConnected);
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task<string?> ResolveBotOpenIdAsync(FeishuSettingsSnapshot settings, CancellationToken ct)
    {
        var cached = Volatile.Read(ref _botInfoCache);
        if (cached is not null && cached.Settings == settings && Clock.Now < cached.ExpiresAt)
            return cached.BotOpenId.Length > 0 ? cached.BotOpenId : null;

        await _botInfoGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _botInfoCache);
            if (cached is not null && cached.Settings == settings && Clock.Now < cached.ExpiresAt)
                return cached.BotOpenId.Length > 0 ? cached.BotOpenId : null;

            var token = await GetTenantTokenAsync(settings, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return null;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{settings.ApiBase}/open-apis/bot/v3/info");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonObject;
            var openId = ((root?["bot"] as JsonObject)?["open_id"]?.GetValue<string>()
                ?? (root?["data"]?["bot"] as JsonObject)?["open_id"]?.GetValue<string>())?.Trim() ?? string.Empty;

            Volatile.Write(ref _botInfoCache, new FeishuBotInfoCache(
                settings,
                openId,
                Clock.Now.AddMinutes(openId.Length > 0 ? 30 : 5)));
            return openId.Length > 0 ? openId : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            _botInfoGate.Release();
        }
    }

    private static bool VerifySignature(string body, string? signature, string? timestamp, string? nonce, string encryptKey)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(nonce)
            || !long.TryParse(timestamp, out var unixSeconds))
        {
            return false;
        }

        DateTimeOffset signedAt;
        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if ((Clock.UtcNow - signedAt).Duration() > SignatureMaxAge)
            return false;

        var raw = timestamp + nonce + encryptKey + body;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private (bool Registered, bool Unavailable) TryRegisterWebhook(string eventKey, string? nonce, bool signed)
    {
        var keys = signed && !string.IsNullOrWhiteSpace(nonce)
            ? new[] { $"nonce:{nonce}", eventKey }
            : new[] { eventKey };
        try
        {
            if (!TryMarkSeen(keys)) return (false, false);
            if (LastErrorCode == "dedup_unavailable") LastErrorCode = null;
            return (true, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastErrorCode = "dedup_unavailable";
            return (false, true);
        }
    }

    private bool TryMarkSeen(IReadOnlyList<string> keys)
    {
        var now = Clock.UtcNow;
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (_seen.Count > SeenCapacity || now.Ticks - last > TimeSpan.FromSeconds(60).Ticks)
        {
            if (Interlocked.CompareExchange(ref _lastPruneTicks, now.Ticks, last) == last)
            {
                foreach (var pair in _seen)
                {
                    if (now - pair.Value > SeenTtl)
                        _seen.TryRemove(new KeyValuePair<string, DateTimeOffset>(pair.Key, pair.Value));
                }
            }
        }

        foreach (var key in keys)
            if (_seen.TryGetValue(key, out var existing) && now - existing <= SeenTtl)
                return false;

        var store = _dedupStore ?? throw new InvalidOperationException("Webhook dedup store is not configured.");
        if (!store.TryRegister(keys, now, SeenTtl)) return false;

        foreach (var key in keys) _seen[key] = now;
        return true;
    }

    private static bool TryGetOptionalString(JsonNode? parent, string name, out string? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var raw)) return false;
        value = raw;
        return true;
    }

    private static bool TryGetOptionalInt(JsonNode? parent, string name, out int? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonValue jsonValue || !jsonValue.TryGetValue<int>(out var raw)) return false;
        value = raw;
        return true;
    }

    private static bool TryGetOptionalObject(JsonNode? parent, string name, out JsonObject? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonObject childObject) return false;
        value = childObject;
        return true;
    }

    private static bool TryGetOptionalArray(JsonNode? parent, string name, out JsonArray? value)
    {
        value = null;
        if (parent is null) return true;
        if (parent is not JsonObject obj) return false;
        if (!obj.TryGetPropertyValue(name, out var child) || child is null) return true;
        if (child is not JsonArray childArray) return false;
        value = childArray;
        return true;
    }
}
