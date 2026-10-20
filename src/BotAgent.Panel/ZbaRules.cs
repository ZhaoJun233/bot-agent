using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;

namespace BotAgent.Panel;

internal static class ZbaRules
{
    private const int MaxSecretBytes = 8192;

    internal static void ValidateKey(ConfigurationOperationKey key)
    {
        if (key.Generation.Value == Guid.Empty || key.IssuedUnixSeconds <= 0 || key.Nonce == Guid.Empty)
            throw new ZbaBadRequestException("invalid_operation_key");
    }

    internal static void ValidateKey(ManagedOperationKey key)
    {
        if (key.Generation.Value == Guid.Empty || key.IssuedUnixSeconds <= 0 || key.Nonce == Guid.Empty)
            throw new ZbaBadRequestException("invalid_operation_key");
    }

    internal static void ValidateSecrets(IReadOnlyList<ZbaSecretEdit> secrets)
    {
        var slots = new HashSet<WritableSecretSlot>();
        foreach (var secret in secrets)
        {
            if (secret is null) throw new ZbaBadRequestException("invalid_json");
            if (!Enum.IsDefined(secret.Slot) || !Enum.IsDefined(secret.Kind))
                throw new ZbaBadRequestException("invalid_secret_slot");
            if (!slots.Add(secret.Slot))
                throw new ZbaBadRequestException("duplicate_secret_slot");
            switch (secret.Kind)
            {
                case SecretEditKind.Replace:
                    if (string.IsNullOrEmpty(secret.Value))
                        throw new ZbaBadRequestException("missing_secret_value");
                    if (Encoding.UTF8.GetByteCount(secret.Value) > MaxSecretBytes)
                        throw new ZbaBadRequestException("secret_too_large");
                    break;
                default:
                    if (secret.Value is not null)
                        throw new ZbaBadRequestException("secret_value_not_allowed");
                    break;
            }
        }
    }
}
