using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("BotAgent.Storage")]

namespace BotAgent.Domain.Configuration;

public enum WritableSecretSlot
{
    ModelApiKey = 0, AgentServerKey = 1, TtsApiKey = 2,
    FeishuAppSecret = 3, FeishuEncryptKey = 4, FeishuVerificationToken = 5
}
public enum SecretEditKind { Keep = 0, Replace = 1, Delete = 2 }

[JsonConverter(typeof(SecretEditOutputGuard))]
public sealed class SecretEdit
{
    private const int MaximumCredentialKeyLength = 128;

    /// <summary>固定槽位；指向已登记凭据时同义保留为 <see cref="WritableSecretSlot.ModelApiKey"/>，以 <see cref="CredentialKey"/> 区分。</summary>
    public WritableSecretSlot Slot { get; }
    /// <summary>非空表示这条编辑指向已登记模型凭据（<c>model:&lt;key&gt;</c>），而不是固定槽位。</summary>
    public ModelCredentialKey? CredentialKey { get; }
    public SecretEditKind Kind { get; }
    internal SensitiveInput? Input { get; }

    private SecretEdit(WritableSecretSlot? slot, ModelCredentialKey? credentialKey, SecretEditKind kind,
        SensitiveInput? input = null)
    {
        var hasSlot = slot is not null;
        var hasKey = credentialKey is not null;
        // 目标必须恰好一个：固定槽位或已登记凭据，二者不可同时缺席/同时给出。
        if (hasSlot == hasKey) throw new ArgumentException("invalid_secret_target");
        if (slot is { } value && !Enum.IsDefined(value)) throw new ArgumentException("invalid_secret_slot");
        if (credentialKey is { } key
            && (string.IsNullOrWhiteSpace(key.Value) || key.Value.Length > MaximumCredentialKeyLength
                || key.Value.Trim() != key.Value || key.Value.Contains('\0')))
            throw new ArgumentException("invalid_model_credential_key");
        Slot = slot ?? WritableSecretSlot.ModelApiKey;
        CredentialKey = credentialKey;
        Kind = kind; Input = input;
    }
    public static SecretEdit Keep(WritableSecretSlot slot) => new(slot, null, SecretEditKind.Keep);
    public static SecretEdit Delete(WritableSecretSlot slot) => new(slot, null, SecretEditKind.Delete);
    public static SecretEdit Replace(WritableSecretSlot slot, SensitiveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new(slot, null, SecretEditKind.Replace, input);
    }
    public static SecretEdit Keep(ModelCredentialKey key) => new(null, key, SecretEditKind.Keep);
    public static SecretEdit Delete(ModelCredentialKey key) => new(null, key, SecretEditKind.Delete);
    public static SecretEdit Replace(ModelCredentialKey key, SensitiveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new(null, key, SecretEditKind.Replace, input);
    }
    internal SecretEdit OwnCopy() => new(CredentialKey is null ? Slot : null, CredentialKey, Kind, Input?.OwnCopy());
    public override string ToString() => "[redacted]";
}

/// <summary>Owns bytes; the only consumption seam is internal to the trusted Storage module.</summary>
[JsonConverter(typeof(SensitiveInputOutputGuard))]
public sealed class SensitiveInput : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _bytes;
    private SensitiveInput(ReadOnlySpan<byte> value) => _bytes = value.ToArray();
    public int ByteLength { get { lock (_gate) return RequireBytes().Length; } }
    public static SensitiveInput FromUtf8(ReadOnlySpan<byte> value) => new(value);
    internal SensitiveInput OwnCopy() { lock (_gate) return new(RequireBytes()); }
    internal byte[] CopyBytes() { lock (_gate) return RequireBytes().ToArray(); }
    private byte[] RequireBytes() => _bytes ?? throw new ObjectDisposedException(nameof(SensitiveInput));
    public override string ToString() => "[redacted]";
    public void Dispose()
    {
        lock (_gate)
        {
            if (_bytes is null) return;
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }
}

internal sealed class SensitiveInputOutputGuard : JsonConverter<SensitiveInput>
{
    public override SensitiveInput Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new JsonException("sensitive_input_requires_closed_codec");
    public override void Write(Utf8JsonWriter writer, SensitiveInput value, JsonSerializerOptions options)
        => throw new JsonException("sensitive_output_forbidden");
}
internal sealed class SecretEditOutputGuard : JsonConverter<SecretEdit>
{
    public override SecretEdit Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new JsonException("secret_edit_requires_closed_codec");
    public override void Write(Utf8JsonWriter writer, SecretEdit value, JsonSerializerOptions options)
        => throw new JsonException("secret_output_forbidden");
}
