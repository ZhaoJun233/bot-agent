using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.Adapters.Panel.Zba;

internal static class ZbaSecretEdits
{
    internal static (ImmutableArray<SecretEdit> Edits, List<SensitiveInput> Owned) ConvertSecrets(
        IReadOnlyList<ZbaSecretEdit>? secrets)
    {
        var owned = new List<SensitiveInput>();
        var edits = ImmutableArray.CreateBuilder<SecretEdit>();
        foreach (var edit in secrets ?? Array.Empty<ZbaSecretEdit>())
        {
            // Core 的 SecretEdit 工厂对非法目标抛 ArgumentException；那会被 ZbaEndpoint 兜成 500。
            // 正常路径上 ZbaRules.ValidateSecrets 已经拦过（400 invalid_secret_slot），
            // 这里只是宿主侧的最后一道防线，保证任何漏网目标仍然是 400 而不是 500。
            if (!IsResolvableSecretTarget(edit))
                throw new ZbaBackendException(400, "invalid_secret_slot", "秘密编辑目标无效。");
            try
            {
                switch (edit.Kind)
                {
                    case SecretEditKind.Replace:
                        var bytes = Encoding.UTF8.GetBytes(edit.Value ?? string.Empty);
                        try
                        {
                            var input = SensitiveInput.FromUtf8(bytes);
                            owned.Add(input);
                            edits.Add(Replace(edit, input));
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(bytes);
                        }

                        break;
                    case SecretEditKind.Delete:
                        edits.Add(Delete(edit));
                        break;
                    default:
                        edits.Add(Keep(edit));
                        break;
                }
            }
            catch (ArgumentException)
            {
                throw new ZbaBackendException(400, "invalid_secret_slot", "秘密编辑目标无效。");
            }
        }

        return (edits.ToImmutable(), owned);
    }

    /// <summary>目标必须恰好是固定槽位或已登记凭据键之一，且自身合法。</summary>
    private static bool IsResolvableSecretTarget(ZbaSecretEdit edit)
    {
        var hasSlot = edit.Slot is not null;
        var hasKey = edit.CredentialKey is not null;
        if (hasSlot == hasKey) return false;
        if (edit.Slot is { } slot) return Enum.IsDefined(slot);
        var key = edit.CredentialKey!;
        return !string.IsNullOrWhiteSpace(key) && key.Length <= 128 && key.Trim() == key && !key.Contains('\0');
    }

    /// <summary>有 <c>credentialKey</c> 时走 Core 的已登记凭据重载，否则维持固定槽位语义。</summary>
    private static SecretEdit Replace(ZbaSecretEdit edit, SensitiveInput input)
        => edit.CredentialKey is { } key
            ? SecretEdit.Replace(new ModelCredentialKey(key), input)
            : SecretEdit.Replace(edit.Slot!.Value, input);

    private static SecretEdit Delete(ZbaSecretEdit edit)
        => edit.CredentialKey is { } key
            ? SecretEdit.Delete(new ModelCredentialKey(key))
            : SecretEdit.Delete(edit.Slot!.Value);

    private static SecretEdit Keep(ZbaSecretEdit edit)
        => edit.CredentialKey is { } key
            ? SecretEdit.Keep(new ModelCredentialKey(key))
            : SecretEdit.Keep(edit.Slot!.Value);

}
