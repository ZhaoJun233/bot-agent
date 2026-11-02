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

/// <summary>Owned write-only legacy input, never a response DTO or an application result.</summary>
internal sealed class LegacySecretPlan : IDisposable
{
    private readonly ImmutableArray<SensitiveInput> _owned;
    public ConfigurationResultCode Code { get; }
    public string? ErrorCode { get; }
    public ImmutableArray<SecretEdit> Secrets { get; }
    public FieldEdit<string> Mood { get; }
    public bool TtsProjectionRequested { get; }
    public ConfigurationPatch? Changes { get; private set; }

    internal void AttachChanges(ConfigurationPatch changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (Code != ConfigurationResultCode.Ok || Changes is not null)
            throw new InvalidOperationException("legacy_plan_already_assigned");
        Changes = changes;
    }

    internal LegacySecretPlan(ConfigurationResultCode code, string? errorCode,
        ImmutableArray<SecretEdit> secrets, ImmutableArray<SensitiveInput> owned,
        FieldEdit<string> mood = default, bool ttsProjectionRequested = false)
    {
        Code = code; ErrorCode = errorCode; Secrets = secrets; _owned = owned;
        Mood = mood; TtsProjectionRequested = ttsProjectionRequested;
    }
    public void Dispose()
    {
        foreach (var input in _owned) input.Dispose();
    }
    public override string ToString() => "[redacted]";
}

/// <summary>Pure fixed-six-slot mapping; no storage, environment, logging or runtime mutation.</summary>

internal static class PanelLegacySecrets
{
    internal static LegacySecretPlan ParseLegacySecrets(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (HasOfficialSecretInput(body))
            return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "official_secret_environment_only");
        string? tokenText = null;
        if (body["feishuVerificationToken"] is { } tokenNode)
        {
            if (tokenNode is not JsonValue token || !token.TryGetValue<string>(out tokenText))
                return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_legacy_secret");
            if (string.IsNullOrWhiteSpace(tokenText))
                return RejectedLegacySecrets(ConfigurationResultCode.LegacyNeedsReview, "verification_token_refresh_required");
        }
        var edits = ImmutableArray.CreateBuilder<SecretEdit>(6);
        var owned = ImmutableArray.CreateBuilder<SensitiveInput>();
        try
        {
            edits.Add(ParseLegacySecretSlot(body, "apiKey", null, WritableSecretSlot.ModelApiKey, true, owned));
            edits.Add(ParseLegacySecretSlot(body, "agentServerKey", null, WritableSecretSlot.AgentServerKey, true, owned));
            edits.Add(ParseLegacySecretSlot(body, "ttsApiKey", "clearTtsApiKey", WritableSecretSlot.TtsApiKey, false, owned));
            edits.Add(ParseLegacySecretSlot(body, "feishuAppSecret", "clearFeishuAppSecret",
                WritableSecretSlot.FeishuAppSecret, false, owned));
            edits.Add(ParseLegacySecretSlot(body, "feishuEncryptKey", "clearFeishuEncryptKey",
                WritableSecretSlot.FeishuEncryptKey, false, owned));
            edits.Add(tokenText is null ? SecretEdit.Keep(WritableSecretSlot.FeishuVerificationToken)
                : OwnedLegacySecret(WritableSecretSlot.FeishuVerificationToken, tokenText, owned));
            var mood = FieldEdit<string>.Keep();
            if (body["mood"] is JsonValue moodValue && moodValue.TryGetValue<string>(out var moodText))
                mood = string.IsNullOrWhiteSpace(moodText) ? FieldEdit<string>.Clear() : FieldEdit<string>.Set(moodText);
            var ttsRequested = edits.Any(edit => edit.Slot == WritableSecretSlot.TtsApiKey && edit.Kind != SecretEditKind.Keep)
                || body["ttsProvider"] is not null || body["ttsApiBase"] is not null || body["ttsModel"] is not null;
            return new(ConfigurationResultCode.Ok, null, edits.MoveToImmutable(), owned.ToImmutable(), mood, ttsRequested);
        }
        catch (ArgumentException)
        {
            foreach (var input in owned) input.Dispose();
            return RejectedLegacySecrets(ConfigurationResultCode.ValidationFailed, "invalid_legacy_secret");
        }
        catch
        {
            foreach (var input in owned) input.Dispose();
            throw;
        }
    }

    internal static bool HasOfficialSecretInput(JsonObject body)
        => body.Any(field => string.Equals(field.Key, "officialAppSecret", StringComparison.OrdinalIgnoreCase)
            || string.Equals(field.Key, "clearOfficialAppSecret", StringComparison.OrdinalIgnoreCase));

    internal static LegacySecretPlan RejectedLegacySecrets(ConfigurationResultCode code, string errorCode)
        => new(code, errorCode, ImmutableArray<SecretEdit>.Empty, ImmutableArray<SensitiveInput>.Empty);

    internal static SecretEdit ParseLegacySecretSlot(JsonObject body, string key, string? clearKey,
        WritableSecretSlot slot, bool emptyDeletes, ImmutableArray<SensitiveInput>.Builder owned)
    {
        if (clearKey is not null && body[clearKey] is JsonValue flag && flag.TryGetValue<bool>(out var clear) && clear)
            return SecretEdit.Delete(slot);
        if (body[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) return SecretEdit.Keep(slot);
        if (string.IsNullOrWhiteSpace(text)) return emptyDeletes ? SecretEdit.Delete(slot) : SecretEdit.Keep(slot);
        return OwnedLegacySecret(slot, text, owned);
    }

    internal static SecretEdit OwnedLegacySecret(WritableSecretSlot slot, string value,
        ImmutableArray<SensitiveInput>.Builder owned)
    {
        var codec = new UTF8Encoding(false, true);
        if (codec.GetByteCount(value) > 16384 || value.Contains('\0')) throw new ArgumentException("invalid_legacy_secret");
        var bytes = codec.GetBytes(value.Trim());
        try
        {
            var input = SensitiveInput.FromUtf8(bytes);
            owned.Add(input);
            return SecretEdit.Replace(slot, input);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
