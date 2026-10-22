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
    public WritableSecretSlot Slot { get; }
    public SecretEditKind Kind { get; }
    internal SensitiveInput? Input { get; }

    private SecretEdit(WritableSecretSlot slot, SecretEditKind kind, SensitiveInput? input = null)
    {
        if (!Enum.IsDefined(slot)) throw new ArgumentException("invalid_secret_slot");
        Slot = slot; Kind = kind; Input = input;
    }
    public static SecretEdit Keep(WritableSecretSlot slot) => new(slot, SecretEditKind.Keep);
    public static SecretEdit Delete(WritableSecretSlot slot) => new(slot, SecretEditKind.Delete);
    public static SecretEdit Replace(WritableSecretSlot slot, SensitiveInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new(slot, SecretEditKind.Replace, input);
    }
    internal SecretEdit OwnCopy() => new(Slot, Kind, Input?.OwnCopy());
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
