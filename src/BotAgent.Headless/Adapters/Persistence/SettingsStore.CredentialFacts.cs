extern alias StorageModule;

using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 配置事务里“凭据事实”的那一半：固定槽位与已登记凭据（<c>model:&lt;key&gt;</c>）两套引用
/// 各自的 configured / usable 判定。拆出来只为守住单文件行数棘轮，行为与
/// <see cref="SettingsStore.ConfigurationParticipant" /> 原实现逐字一致。
/// </summary>
public sealed partial class SettingsStore
{
    internal sealed partial class ConfigurationParticipant
    {
        private readonly record struct PrimaryCredentialFacts(
            bool Known, bool Configured, bool KeyUsable, CredentialSource Source);

        /// <summary>已登记凭据的事实：先看本次提交待写入的值，未提交的引用回落到库里已有的行。</summary>
        private static IReadOnlyDictionary<string, PrimaryCredentialFacts> ObserveRegisteredCredentialFacts(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            ImmutableArray<StorageConfiguration.SecretMutation> proposedMutations,
            IReadOnlyList<PersistedModelProvider> providers)
        {
            var facts = new Dictionary<string, PrimaryCredentialFacts>(StringComparer.Ordinal);
            foreach (var mutation in proposedMutations)
            {
                if (mutation.CredentialKey is not { } key || !Enum.IsDefined(mutation.Kind)) continue;
                // Delete 之后引用仍在，但行已经没了：configured=false 正是要报给用户的结论。
                var configured = mutation.Kind != SecretEditKind.Delete
                    && !string.IsNullOrWhiteSpace(mutation.NormalizedValue);
                facts[ModelCredentialStore.Name(key)] = new(true, configured,
                    configured && IsUsableKeyValue(mutation.NormalizedValue), CredentialSource.Database);
            }
            foreach (var provider in providers)
            {
                if (!ModelProviderStore.TryGetRegisteredCredentialKey(provider.SecretKeyRef, out var key)) continue;
                var name = ModelCredentialStore.Name(key);
                if (facts.ContainsKey(name)) continue;
                var stored = ModelCredentialStore.LoadInTransaction(connection, key);
                facts[name] = new(true, !string.IsNullOrWhiteSpace(stored), IsUsableKeyValue(stored), CredentialSource.Database);
            }
            return facts;
        }

        /// <summary>与固定槽位同一套可用性口径：非空且不是被误粘进来的 URL。</summary>
        private static bool IsUsableKeyValue(string? value)
            => !string.IsNullOrWhiteSpace(value)
                && !value!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
