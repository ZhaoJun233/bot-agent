using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Services.Platforms;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private const int MaxFeishuWebhookBodyBytes = 1_048_576;
    private static readonly (string PlatformId, string AccountScope, string DisplayName, string Tag, Domain.Platforms.PlatformCapabilities Capabilities)[] StandardPlatforms =
    [
        (Domain.Platforms.PlatformId.QqPrivate, Domain.Platforms.AccountScope.Legacy, "QQ私域", "私域", Domain.Platforms.PlatformCapabilities.QqOneBot),
        (Domain.Platforms.PlatformId.QqOfficial, Domain.Platforms.AccountScope.Legacy, "QQ官方", "官方", Domain.Platforms.PlatformCapabilities.QqOfficial),
        (Domain.Platforms.PlatformId.Feishu, Domain.Platforms.AccountScope.Default, "飞书", "飞书", Domain.Platforms.PlatformCapabilities.FeishuTextOnly),
        (Domain.Platforms.PlatformId.Local, Domain.Platforms.AccountScope.Legacy, "本地通道", "本地", Domain.Platforms.PlatformCapabilities.Local),
    ];

    private async Task HandlePlatformsAsync(HttpListenerContext context)
    {
        var rawSnapshots = _platformRegistry?.GetSnapshots() ?? Array.Empty<Domain.Ports.PlatformStatusSnapshot>();
        var policies = _platformPolicies ?? new PlatformPolicyResolver(_box, _platformRegistry);
        var settings = _box.Current;
        // Keep every real instance; an account display row is never an authenticated source.
        var allSnapshots = rawSnapshots.ToList();
        foreach (var std in StandardPlatforms)
        {
            if (rawSnapshots.Any(live => Domain.Platforms.PlatformId.Normalize(live.PlatformId) == std.PlatformId
                && live.AccountScope == std.AccountScope)) continue;
            allSnapshots.Add(new Domain.Ports.PlatformStatusSnapshot(std.PlatformId, std.AccountScope,
                std.DisplayName, std.Tag, false, false, std.Capabilities, null));
        }
        var instanceCounts = rawSnapshots.GroupBy(snapshot => (Domain.Platforms.PlatformId.Normalize(snapshot.PlatformId),
            snapshot.AccountScope, snapshot.InstanceId)).ToDictionary(group => group.Key, group => group.Count());

        var arr = new JsonArray();
        foreach (var s in allSnapshots)
        {
            var loaded = rawSnapshots.Contains(s);
            var contextKey = (Domain.Platforms.PlatformId.Normalize(s.PlatformId), s.AccountScope, s.InstanceId);
            var exact = loaded && instanceCounts.GetValueOrDefault(contextKey) == 1;
            var actual = new Domain.Platforms.PlatformContext(s.PlatformId, s.AccountScope, s.InstanceId);
            var policy = exact ? policies.Resolve(actual) : null;
            var accountView = PlatformSwitchSettings.Read(settings, s.PlatformId, s.AccountScope);
            var configuredChat = exact ? settings.RuntimePlatformPolicies.HasValue
                ? PlatformSwitchSettings.ResolvePolicyRules(settings, actual,
                    System.Collections.Immutable.ImmutableArray<Domain.Configuration.ActionId>.Empty).ChatEnabled == Domain.Configuration.ToggleRule.Enabled
                : accountView.ChatEnabled : !loaded && accountView.ChatEnabled;
            var enabled = exact ? policy!.Enabled : !loaded && accountView.Enabled;
            arr.Add(new JsonObject
            {
                ["platformId"] = s.PlatformId,
                ["accountScope"] = s.AccountScope,
                ["instanceId"] = s.InstanceId,
                ["displayName"] = s.DisplayName,
                ["tag"] = s.Tag,
                ["enabled"] = enabled,
                ["effectiveEnabled"] = enabled,
                ["configuredChatEnabled"] = configuredChat,
                ["chatEnabled"] = enabled && configuredChat,
                ["connected"] = exact && s.Connected,
                ["restartRequired"] = enabled && !loaded,
                ["lastErrorCode"] = s.LastErrorCode,
                ["reasons"] = policy is not null
                    ? new JsonArray(policy.Reasons.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray())
                    : new JsonArray(JsonValue.Create(loaded ? "ambiguous_platform_instance" : "platform_unregistered")),
                ["capabilities"] = new JsonObject
                {
                    ["supportsText"] = s.Capabilities.SupportsText,
                    ["supportsImage"] = s.Capabilities.SupportsImage,
                    ["supportsVoice"] = s.Capabilities.SupportsVoice,
                    ["supportsQuote"] = s.Capabilities.SupportsQuote,
                    ["supportsRecall"] = s.Capabilities.SupportsRecall,
                    ["supportsGroup"] = s.Capabilities.SupportsGroup,
                    ["supportsDirect"] = s.Capabilities.SupportsDirect,
                    ["supportsThread"] = s.Capabilities.SupportsThread,
                    ["supportsStickers"] = s.Capabilities.SupportsStickers,
                    ["supportsMusic"] = s.Capabilities.SupportsMusic,
                    ["supportsPoke"] = s.Capabilities.SupportsPoke,
                },
            });
        }

        var feishuLoaded = _feishuGateway is not null;
        var feishuEnabled = PlatformSwitchSettings.Read(settings, Domain.Platforms.PlatformId.Feishu,
            Domain.Platforms.AccountScope.Default).Enabled;
        var feishuConfigured = _feishuGateway?.IsConfigured
            ?? (!string.IsNullOrWhiteSpace(_box.Current.FeishuAppId)
                && !string.IsNullOrWhiteSpace(_box.Current.FeishuAppSecret)
                && (!string.IsNullOrWhiteSpace(_box.Current.FeishuEncryptKey)
                    || !string.IsNullOrWhiteSpace(_box.Current.FeishuVerificationToken)));
        var feishuRestartRequired = feishuEnabled != feishuLoaded;
        var feishuObj = new JsonObject
        {
            ["enabled"] = feishuEnabled,
            ["configured"] = feishuConfigured,
            ["loaded"] = feishuLoaded,
            ["effectiveEnabled"] = feishuEnabled && feishuLoaded,
            ["restartRequired"] = feishuRestartRequired,
            ["connected"] = _feishuGateway?.IsConnected ?? false,
            ["outboxCount"] = _feishuGateway?.Outbox.Count ?? 0,
        };

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["platforms"] = arr,
            ["feishu"] = feishuObj,
        });
    }

    private async Task HandleFeishuWebhookAsync(HttpListenerContext context)
    {
        if (_feishuGateway is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject
            {
                ["error"] = "feishu_gateway_not_loaded",
                ["reason"] = "restart_required",
            });
            return;
        }

        if (!_box.Current.FeishuEnabled)
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "feishu_channel_disabled",
                ["reason"] = "feishu_disabled",
            });
            return;
        }

        if (context.Request.ContentLength64 > MaxFeishuWebhookBodyBytes)
        {
            await WriteJsonAsync(context, 413, new JsonObject
            {
                ["error"] = "body_too_large",
                ["maxBytes"] = MaxFeishuWebhookBodyBytes,
            });
            return;
        }

        if (!await _feishuWebhookSlots.WaitAsync(0, _cts.Token).ConfigureAwait(false))
        {
            await WriteJsonAsync(context, 429, new JsonObject
            {
                ["error"] = "webhook_overloaded",
            });
            return;
        }

        try
        {
            var body = await ReadFeishuBodyAsync(context.Request.InputStream, context.Request.ContentEncoding, _cts.Token)
                .ConfigureAwait(false);
            if (body is null)
            {
                await WriteJsonAsync(context, 413, new JsonObject
                {
                    ["error"] = "body_too_large",
                    ["maxBytes"] = MaxFeishuWebhookBodyBytes,
                });
                return;
            }

            var sig = context.Request.Headers["X-Lark-Signature"];
            var ts = context.Request.Headers["X-Lark-Request-Timestamp"];
            var nonce = context.Request.Headers["X-Lark-Request-Nonce"];
            var verificationToken = context.Request.Headers["X-Lark-Verification-Token"];
            var (_, statusCode, responseBody) = await _feishuGateway.HandleWebhookAsync(
                body, sig, ts, nonce, verificationToken, _cts.Token).ConfigureAwait(false);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            var bytes = System.Text.Encoding.UTF8.GetBytes(responseBody);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // 宿主停止时不再尝试写响应。
        }
        finally
        {
            _feishuWebhookSlots.Release();
        }
    }

    private static async Task<string?> ReadFeishuBodyAsync(
        Stream input,
        System.Text.Encoding? encoding,
        CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        await using var memory = new MemoryStream();
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaxFeishuWebhookBodyBytes) return null;
            await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return (encoding ?? System.Text.Encoding.UTF8).GetString(memory.ToArray());
    }
}
