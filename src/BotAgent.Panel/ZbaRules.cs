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

    /// <summary>
    /// 新 Web 只允许访问工作台（<c>panel:</c>）会话：群聊会话名可能由模型按聊天内容自动综结，
    /// 不通过 ZBA 的会话发现暴露。格式错误 400，来源越界 403。
    /// </summary>
    internal static void ValidatePanelSourceKey(string? sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 256 || sourceKey.Any(char.IsControl))
            throw new ZbaBadRequestException("invalid_source_key");
        if (!sourceKey.StartsWith("panel:", StringComparison.Ordinal))
            throw new ZbaBackendException(403, "source_key_not_allowed", "会话来源不在允许范围内。");
    }

    internal static void ValidateIdempotencyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 128
            || !key.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or '~'))
        {
            throw new ZbaBadRequestException("invalid_idempotency_key");
        }
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
