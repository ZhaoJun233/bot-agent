using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private static readonly SemaphoreSlim ConfigurationParseAdmission = new(4, 4);
    // 反射接缝：tests/BotAgent.ReviewRemediationProbe/ConfigurationContractTests.cs 用 GetMethod 调这三个私有方法，
    // 拆分后保留同名同签名的转发壳，探针与外部行为都不需要改。
    private static ConfigurationPatch ParseLegacyScalarFields(JsonObject body)
        => PanelLegacyScalarFields.ParseLegacyScalarFields(body);

    private static LegacySecretPlan ParseLegacyConfiguration(JsonObject body, BotAgent.Platforms.PlatformOptions trustedOptions)
        => PanelLegacyConfiguration.ParseLegacyConfiguration(body, trustedOptions);

    private static LegacySecretPlan ParseLegacySecrets(JsonObject body)
        => PanelLegacySecrets.ParseLegacySecrets(body);

    private static JsonSerializerOptions ConfigurationJson => PanelConfigurationRequestValidation.ConfigurationJson;

    private static int ConfigurationHttpStatus(ConfigurationResultCode code) => code switch
    {
        ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges => 200,
        ConfigurationResultCode.Forbidden => 403,
        ConfigurationResultCode.ValidationFailed or ConfigurationResultCode.UnsupportedContractRevision => 400,
        ConfigurationResultCode.CapacityExceeded => 429,
        ConfigurationResultCode.StorageUnavailable or ConfigurationResultCode.CommitFailed or ConfigurationResultCode.UnknownCommit => 503,
        ConfigurationResultCode.NotFound => 404,
        _ => 409,
    };
    private Task ConfigurationError(HttpListenerContext context, int status, string error,
        ConfigurationResultCode code = ConfigurationResultCode.ValidationFailed, bool refresh = false)
        => WriteJsonAsync(context, status, new JsonObject { ["error"] = error, ["code"] = (int)code, ["refreshRequired"] = refresh });
    private async Task HandleSettingsSaveAsync(HttpListenerContext context)
    {
        if (!IsAuthorized(context) || !IsSameOrigin(context))
        { await ConfigurationError(context, 403, "forbidden", ConfigurationResultCode.Forbidden); return; }
        var operations = context.Request.QueryString.GetValues("configuration");
        var caller = new ConfigurationCaller("headless-panel", "panel-administrator", "bot",
            ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
        using var scope = _settingsHotReload.ConfigurationAuthorization.Enter(caller, () => IsAuthorized(context) && IsSameOrigin(context));
        if (operations is not null)
        {
            if (operations.Length != 1 || operations[0] is not ("read" or "save" or "find"))
            { await ConfigurationError(context, 400, "invalid_configuration_operation"); return; }
            await HandleConfigurationRequestAsync(context, operations[0]); return;
        }
        var body = await ReadJsonAsync(context);
        if (body is not JsonObject submitted)
        { await ConfigurationError(context, 400, "invalid_json"); return; }
        using var plan = ParseLegacyConfiguration(submitted, _settings);
        if (plan.Code != ConfigurationResultCode.Ok)
        {
            var refresh = plan.Code == ConfigurationResultCode.LegacyNeedsReview;
            await ConfigurationError(context, ConfigurationHttpStatus(plan.Code), refresh ? "refresh_required"
                : plan.ErrorCode ?? "invalid_configuration", plan.Code, refresh); return;
        }
        ConfigurationOperationKey? key = null;
        var supplied = context.Request.Headers.GetValues("X-BotAgent-Configuration-Key");
        if (supplied is not null)
        {
            try
            {
                if (supplied.Length != 1 || supplied[0].Contains(',')) throw new JsonException();
                key = JsonSerializer.Deserialize<ConfigurationOperationKey>(JsonSerializer.Serialize(supplied[0]), ConfigurationJson);
            }
            catch (JsonException) { await ConfigurationError(context, 400, "invalid_configuration_key"); return; }
        }
        var outcome = await _settingsHotReload.SaveLegacyConfigurationAsync(key, plan.Changes!, plan.Secrets, plan.Mood, plan.TtsProjectionRequested);
        var result = outcome.ConfigurationResult;
        JsonObject payload;
        if (result.Code is ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges) payload = BuildSettingsPayload();
        else payload = new JsonObject { ["error"] = "configuration_rejected", ["code"] = (int)result.Code,
            ["refreshRequired"] = result.Code == ConfigurationResultCode.LegacyNeedsReview };
        payload["configurationResult"] = JsonSerializer.SerializeToNode(result, ConfigurationJson);
        payload["legacyActionResults"] = JsonSerializer.SerializeToNode(outcome.LegacyActionResults, ConfigurationJson);
        await WriteJsonAsync(context, ConfigurationHttpStatus(result.Code), payload);
    }
    private async Task HandleConfigurationRequestAsync(HttpListenerContext context, string operation)
    {
        if (!await ConfigurationParseAdmission.WaitAsync(0))
        { await ConfigurationError(context, 429, "configuration_parse_busy", ConfigurationResultCode.CapacityExceeded); return; }
        byte[]? bytes = null;
        var storageStarted = false;
        try
        {
            const int maximum = 1048576;
            if (context.Request.ContentLength64 > maximum)
            { await ConfigurationError(context, 413, "configuration_body_too_large", ConfigurationResultCode.CapacityExceeded); return; }
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            try
            {
                int size;
                while ((size = await context.Request.InputStream.ReadAsync(chunk)) != 0)
                {
                    if (buffer.Length + size > maximum)
                    { await ConfigurationError(context, 413, "configuration_body_too_large", ConfigurationResultCode.CapacityExceeded); return; }
                    buffer.Write(chunk, 0, size);
                }
                bytes = buffer.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(chunk); CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
            var raw = new UTF8Encoding(false, true).GetString(bytes);
            PanelConfigurationRequestValidation.ValidateConfigurationUnicode(raw);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            PanelConfigurationRequestValidation.ValidateConfigurationDuplicates(document.RootElement);
            var root = document.RootElement;
            if (operation == "read")
            {
                PanelConfigurationRequestValidation.ConfigurationMembers(root, "kind");
                if (!root.GetProperty("kind").TryGetInt32(out var kind) || kind is < 0 or > 1) throw new JsonException();
                storageStarted = true;
                var read = await _settingsHotReload.ReadConfigurationAsync((ConfigurationReadKind)kind);
                if (read.Generation.Value == Guid.Empty)
                { await ConfigurationError(context, ConfigurationHttpStatus(read.Code), "configuration_unavailable", read.Code); return; }
                await WriteJsonAsync(context, ConfigurationHttpStatus(read.Code), JsonSerializer.SerializeToNode(read, ConfigurationJson)!); return;
            }
            if (operation == "find")
            {
                PanelConfigurationRequestValidation.ConfigurationMembers(root, "key");
                var key = JsonSerializer.Deserialize<ConfigurationOperationKey>(root.GetProperty("key"), ConfigurationJson);
                storageStarted = true;
                var found = await _settingsHotReload.FindConfigurationAsync(key);
                await WriteJsonAsync(context, ConfigurationHttpStatus(found.ConfigurationResult.Code),
                    JsonSerializer.SerializeToNode(found, ConfigurationJson)!); return;
            }
            var (request, owner) = PanelConfigurationRequestValidation.ParseConfigurationSave(root);
            using (owner)
            {
                storageStarted = true;
                var result = await _settingsHotReload.SaveConfigurationAsync(request);
                await WriteJsonAsync(context, ConfigurationHttpStatus(result.Code), JsonSerializer.SerializeToNode(result, ConfigurationJson)!);
            }
        }
        catch (Exception error) when (!storageStarted && (error is JsonException or ArgumentException or InvalidOperationException or DecoderFallbackException or OverflowException))
        { await ConfigurationError(context, 400, "invalid_configuration"); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); ConfigurationParseAdmission.Release(); }
    }
}
