extern alias StorageModule;

using BotAgent.Domain.Configuration;
using System.Collections.Immutable;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;
using ConfigurationModule = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Panel.Zba;
using BotAgent.Adapters.Model;
using BotAgent.Adapters.Platforms;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Model;
using BotAgent.Services.Platforms;
using BotAgent.Services.Resilience;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using BotAgent.Services.Links;
using BotAgent.Services.Local;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.Poke;
using BotAgent.Services.Permissions;
using BotAgent.Services.Participation;
using BotAgent.Services.Panel;
using BotAgent.Services.Settings;
using BotAgent.Services.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Services.Mcp;
namespace BotAgent.Host;

internal static partial class CompositionRoot
{
    private static ImmutableArray<SecretMetadata> ConfigurationSecretMetadata(
        Microsoft.Data.Sqlite.SqliteConnection connection, AppSettings current,
        ImmutableArray<ConfigurationModule.SecretMutation> mutations)
    {
        var result = ImmutableArray.CreateBuilder<SecretMetadata>();
        foreach (var slot in Enum.GetValues<WritableSecretSlot>())
        {
            // 已登记模型凭据的变更没有自己的槽位（默认落在 ModelApiKey 上），必须排除，否则会污染 apiKey 的事实。
            var mutation = mutations.FirstOrDefault(value => value.CredentialKey is null && value.Slot == slot);
            var kind = mutation.Kind;
            var stored = StorageSecrets.LoadInTransaction(connection, slot);
            var configured = kind == SecretEditKind.Replace || kind == SecretEditKind.Keep && !string.IsNullOrWhiteSpace(stored);
            var source = configured ? CredentialSource.Database : CredentialSource.None;
            string? environment = slot switch
            {
                WritableSecretSlot.FeishuAppSecret or WritableSecretSlot.FeishuEncryptKey => BotConfig.FixedConfigurationEnvironmentSecret(slot),
                WritableSecretSlot.ModelApiKey when kind == SecretEditKind.Delete =>
                    BotConfig.FixedModelEnvironmentKey(),
                WritableSecretSlot.ModelApiKey when kind == SecretEditKind.Keep && string.IsNullOrWhiteSpace(current.ApiKeyOverride) => current.ApiKey,
                WritableSecretSlot.AgentServerKey when kind == SecretEditKind.Delete => EnvironmentAliases.Get("BOTAGENT_AGENT_SERVER_KEY", "QQCHAT_AGENT_SERVER_KEY")?.Trim(),
                WritableSecretSlot.AgentServerKey when kind == SecretEditKind.Keep && string.IsNullOrWhiteSpace(current.AgentServerApiKeyOverride) => current.AgentServerApiKey,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(environment)) { configured = true; source = CredentialSource.Environment; }
            result.Add(new(slot, configured, source));
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// ① 装配阶段一：配置发布点与全进程共用的无状态服务（#110 第 2 步拆出）。
    ///
    /// 这一段是**所有后续阶段的上游**：组件拿到的都是 <see cref="SettingsBox"/>（读当前值），
    /// 不是配置快照；配置参与者与配置运行时也必须在任何写路径之前激活。
    /// </summary>
    private static void AssembleCore(Assembly a)
    {
        var settings = a.Settings;
        // 配置的唯一发布点：所有组件都从它读"当前生效的那一份"。
        // 面板热更新换的是它背后的实例（不是就地改共享对象），这样在途的那一轮不会读到半新半旧的配置
        // （review-findings #4）。见 SettingsBox。
        a.Box = new SettingsBox(settings);

        // 密钥库：无状态（每次都直接读写库里那几条），全进程共用一个实例只是为了少造对象。
        a.Secrets = new SecretsStore();
        // 配置库：同样无状态；热更新与面板共用它。
        a.SettingsStore = new SettingsStore();

        // Provider 注册表只登记非敏感路由元数据；实际密钥仍由环境变量/密钥库提供。
        // 启动时同步 primary 的地址与模型名，但不覆盖已有熔断状态。
        a.ProviderStore = new ModelProviderStore();
        a.Authorization = new SettingsHotReload.ConfigurationRequestAuthorization();
        // 当前"装配事实"（#128）：启动时载入一次，之后每次保存配置由模型投影步骤整体替换。
        // 路由解析、回读投影与托管能力描述都从这里读 —— 全进程只有这一份，不再各自抄启动快照。
        a.ModelRegistry = new ModelProviderRegistry(Array.Empty<PersistedModelProvider>());
        a.Ui = new PanelNotifier();
        a.Quotas = new TenantQuotaStore();
        a.PluginRuntime = new PluginRuntime(Path.Combine(AppPaths.DataDir, "plugins"), a.Ui.EmitLog, a.Quotas);
        // 注册表投影的读口：每次保存都重新读库（在同一个事务里），不缓存。
        a.ReadRegistered = (connection, kind) =>
        {
            var providers = ModelProviderStore.LoadAllInTransaction(connection);
            return new RegisteredConfigurationView(a.PluginRuntime.ReadConfiguration(connection),
                SettingsStore.ProjectRegisteredProviderViews(connection, providers, a.ModelRegistry.Captured, kind),
                // 已登记凭据只投影“存在/可用”，正文永不离开密钥库。
                SettingsStore.ProjectModelCredentials(connection, providers));
        };
        a.Participant = new SettingsStore.ConfigurationParticipant(a.Box, a.Authorization.Reauthorize,
            (connection, mutations) => ConfigurationSecretMetadata(connection, a.Box.Current, mutations), a.ReadRegistered,
            id => BotConfig.ConfigurationEffect(id) == FieldEffect.Restart
                || id == ConfigurationFieldId.Model && string.IsNullOrWhiteSpace(a.Box.Current.ModelOverride)
                || id == ConfigurationFieldId.ModelBaseUrl && string.IsNullOrWhiteSpace(a.Box.Current.ModelBaseUrlOverride)
                    ? ConfigurationValueSource.Unclassified : ConfigurationValueSource.UserConfiguration,
            BotConfig.ConfigurationEffect, true);
        a.Configuration = ConfigurationBridge.Activate(BotAgent.Adapters.Time.ClockBindings.Source, a.Participant);
        var initialized = a.SettingsStore.LoadInitializedRuntimeSnapshot(settings);
        a.Settings = a.Box.Apply(next =>
        {
            next.FeishuVerificationToken = initialized.FeishuVerificationToken;
            next.RuntimePlatformPolicies = initialized.RuntimePlatformPolicies;
            next.RuntimePlatformPolicyExplicitFields = initialized.RuntimePlatformPolicyExplicitFields;
            next.RuntimePlatformPolicyBaselineSources = initialized.RuntimePlatformPolicyBaselineSources;
        });
        a.ModelRegistry.Initialize(a.ProviderStore.LoadAll().ToArray());

        a.RiskBackoff = new ProtocolRiskBackoff(msg => FileLog.Write("OneBot", msg));
        a.InboundDedup = new FeishuWebhookDedupStore();
    }
}
