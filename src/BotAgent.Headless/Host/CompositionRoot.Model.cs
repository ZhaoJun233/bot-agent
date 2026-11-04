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
    /// <summary>
    /// 模型出网客户端的"天花板"：单次调用真正等多久由传输层按 provider 的 timeoutSeconds
    /// 逐次下发（1..300 秒，见 <c>ProviderRequestBudget</c>），这里只给 HttpClient 一个不会
    /// 提前掐断长请求的上限。设成 300 是因为面板允许的最大值就是 300。
    /// </summary>
    private static readonly TimeSpan ModelHttpCeiling = TimeSpan.FromSeconds(300);

    /// <summary>
    /// 模型与媒体层：模型客户端（三个出网分别 60s / 聊天超时 / 8s）、两个仓储、表情包运营、本机 agent 桥、
    /// 语音、音乐与链接预览、联网研究。都是"拿到配置就能造出来"的件，与用例之间的接线无关（见 Build）。
    /// </summary>
    private static (OpenAiClient Brain, ConversationStore Store, MemberProfileStore Profiles, StickerService Stickers,
        VoiceUseCase Voice, MusicUseCase Music, LinkPreviewer Links, ResearchUseCase Research)
        BuildModelAndMediaLayer(AppSettings settings, SettingsBox settingsBox, IQqChatSource source, ISecretsRepository secrets, ModelProviderStore providerStore,
            ModelProviderRegistry modelRegistry, ToolCircuitBreaker ttsBreaker, ToolCircuitBreaker searchBreaker)
    {
        var allowPrivateOutbound = EnvironmentAliases.Get("BOTAGENT_ALLOW_PRIVATE_IMAGE_HOSTS", "QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS") == "1";
        // 模型那条路的出网：辅助调用 60 秒、聊天按 QQCHAT_MODEL_TIMEOUT_SECONDS（默认 120）、图片下载 8 秒。
        // 客户端只把 300 秒当"天花板"——真正的每次调用预算由传输层按 provider 的 timeoutSeconds
        // 或上面这两个默认值逐次下发（见 ModelTransport/OpenAiClient 的 auxiliaryTimeout），
        // 否则面板里把超时设到 120 秒以上也永远打不到线上。
        var modelAuxHttp = new HttpFetcher(ModelHttpCeiling, msg => FileLog.Write("Net", msg), "model-aux");
        var modelChatHttp = new HttpFetcher(ModelHttpCeiling, msg => FileLog.Write("Net", msg), "model-chat");
        var imageHttp = new HttpFetcher(
            TimeSpan.FromSeconds(8),
            msg => FileLog.Write("Net", msg),
            "image",
            allowAutoRedirect: false,
            rejectPrivateDestinations: !allowPrivateOutbound);

        // 图片下载器与传输层（模型那条路的两个 IO 件）：都在这里造，客户端只拿端口。
        var imageDownloader = new ImageDownloader(imageHttp);
        var transport = new ModelTransport(
            settingsBox, modelChatHttp, modelAuxHttp, imageDownloader, OpenAiClient.ModelTimeout(),
            TimeSpan.FromSeconds(60));

        var persistedProviders = modelRegistry.Captured;
        var providerRoutes = persistedProviders
            .Select(p => TryBuildProviderRoute(p, settings, secrets))
            .Where(route => route is not null)
            .Cast<ModelProviderRoute>()
            .ToDictionary(route => route.ProviderId, StringComparer.Ordinal);
        var providerRunner = new ProviderFailoverRunner(
            persistedProviders.Select(p => new ProviderCandidate(p.Id, p.Priority, p.IsEnabled)),
            initialSnapshots: persistedProviders.Select(ToCircuitSnapshot),
            onSnapshotChanged: snapshot =>
            {
                try
                {
                    providerStore.SaveCircuit(snapshot);
                }
                catch (Exception ex)
                {
                    FileLog.Warn("Model", $"Provider 熔断状态持久化失败（仅影响恢复台账）：{ex.GetType().Name}");
                }
            });

        // 注册表热重建（#128）：执行器实例只造一次，候选表由模型投影步骤整体换掉；
        // 上面这份 providerRoutes 只是"没有解析器时的兜底"，真正每次请求用的路由由下面这个解析器现取。
        modelRegistry.Attach(providerRunner);

        var brain = new OpenAiClient(settingsBox, modelAuxHttp, modelChatHttp, imageDownloader, transport,
            providerRunner,
            providerRoutes,
            providerId =>
            {
                var provider = modelRegistry.Captured.FirstOrDefault(p =>
                    string.Equals(p.Id, providerId, StringComparison.Ordinal));
                return provider is null ? null : TryBuildCurrentProviderRoute(provider, settingsBox.Current);
            },
            auxiliaryTimeout: TimeSpan.FromSeconds(60))
        {
            BotIdentity = string.IsNullOrWhiteSpace(settings.NormalizedUin) ? null : settings.NormalizedUin,
            BotPersona = settings.BotPersona,
            AiDesire = settings.AiDesire,
            SuitabilityThreshold = settings.SuitabilityThreshold,
            MaxContextMessages = settings.MaxContextMessages // 漏了这行会让上下文窗口在面板修改后不同步
        };

        var store = new ConversationStore();
        var profiles = new MemberProfileStore();

        // 表情包运营（收图/说明/自巡检/频率门）：状态与定时器都在它自己身上，装配在这里、只装配一次
        var stickers = new StickerService(new StickerStore(), brain, source, settingsBox, msg => FileLog.Write("Sticker", msg));
        // 表情包库建好即就位：巡检定时器（BotScheduler.RebuildIfNeeded → EnsureTimer）与面板首屏
        // 都要在**任何一轮回复之前**读到它。载入属启动顺序，所以放在装配点（§6.1）。
        stickers.Load(AppPaths.RuntimeRoot);

        // 本机 Agent 桥已按方案退役：// 命令只走服务器内置 agent（ServerAgentRunner）。
        // 语音（TTS）：客户端、频率门、面板试听都在这个用例里（HttpClient 也只在这里造一个）
        var voiceHttp = new HttpFetcher(TimeSpan.FromSeconds(30), msg => FileLog.Write("Net", msg), "voice");
        var voice = new VoiceUseCase(
            new VoiceService(voiceHttp, () => settingsBox.Current, secrets, msg => FileLog.Write("Voice", msg), ttsBreaker),
            settingsBox,
            msg => FileLog.Write("Voice", msg));

        // 音乐（听歌/分享）与链接预览：各自的 HttpClient 在这里造，依赖顺着构造函数往下传。
        // 音乐数据放 data/music/：台账常驻，音频默认分析完就丢（只在 MusicKeepAudio 打开时才留在 audio/）。
        var mediaHttp = new HttpFetcher(TimeSpan.FromSeconds(45), msg => FileLog.Write("Net", msg), "media");
        var musicStore = new MusicStore(() => settingsBox.Current.MusicLibraryMax, msg => FileLog.Write("Music", msg));
        var netease = new NeteaseMusicClient(
            mediaHttp,
            () => settingsBox.Current.NeteaseCookie,
            () => settingsBox.Current.NeteaseBaseUrl,
            msg => FileLog.Write("Music", msg));
        var audioResolver = new MusicAudioResolver(
            mediaHttp,
            () => settingsBox.Current.MusicSources.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            () => Math.Clamp(settingsBox.Current.MusicBitrate, 32, 320),
            () => Math.Clamp(settingsBox.Current.MusicMaxDownloadMb, 1, 64) * 1024 * 1024,
            msg => FileLog.Write("Music", msg));
        var music = new MusicUseCase(
            new MusicService(
                musicStore, netease, audioResolver, brain, () => settingsBox.Current,
                Path.Combine(AppPaths.DataDir, "music", "audio"), new AudioCache(), msg => FileLog.Write("Music", msg)),
            source,
            msg => FileLog.Write("Music", msg));
        var linkHttp = new HttpFetcher(
            TimeSpan.FromSeconds(45),
            msg => FileLog.Write("Net", msg),
            "links",
            allowAutoRedirect: false,
            rejectPrivateDestinations: !allowPrivateOutbound);
        var links = new LinkPreviewer(linkHttp, () => settingsBox.Current, msg => FileLog.Write("Links", msg));

        // 联网研究（搜索 / 读页面）：它自己的 HttpClient 超时给宽松点（检索要等上游模型回话）
        var researchHttp = new HttpFetcher(TimeSpan.FromSeconds(60), msg => FileLog.Write("Net", msg), "search");
        var researchPageHttp = new HttpFetcher(
            TimeSpan.FromSeconds(60),
            msg => FileLog.Write("Net", msg),
            "search-page",
            allowAutoRedirect: false,
            rejectPrivateDestinations: !allowPrivateOutbound);
        var research = new ResearchUseCase(
            new WebSearchService(researchHttp, () => settingsBox.Current, msg => FileLog.Write("Search", msg), searchBreaker, researchPageHttp),
            msg => FileLog.Write("Search", msg));


        return (brain, store, profiles, stickers, voice, music, links, research);
    }

    /// <summary>
    /// 模型投影步：把库里刚提交的供应商行**真的装配进运行期**，不再只是"检查一下然后报要重启"（#128）。
    ///
    /// 做法：同一事务里读 desired → 逐条重建路由 → 换掉故障转移候选表与注册表快照。
    /// 为什么现在可以全热：路由（地址/模型名/协议/选项/密钥引用）由 <see cref="BuildProviderRoute"/> 每次现拼，
    /// 传输层按路由逐请求取端点，进程里没有任何按供应商缓存的长生命周期对象（#103 已把密钥降为请求级）。
    /// 因此旧的重启条件（启用位 / 优先级 / 协议 / 选项 / 非 primary 的地址与模型名）全部变成可热应用。
    ///
    /// 失败口径：发布注册表失败时保留旧快照，如实回 <c>Failed</c>；不是一个可保证重启修复的限制。
    /// desired 已提交但 runtime 未收敛，逐请求的快照/凭据边界仍可安全拒绝调用，不能承诺旧实例继续服务。
    /// </summary>
    private static ApplyStepReport InspectModelProjection(ConfigurationVersion expected, ModelProviderRegistry registry,
        SettingsBox settingsBox, ISecretsRepository secrets)
    {
        using var connection = AppDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ConfigurationBridge.ReadConfigurationVersion(connection) != expected)
            throw new InvalidOperationException("configuration_superseded");
        try
        {
            var desired = ModelProviderStore.LoadAllInTransaction(connection).ToArray();
            var current = settingsBox.Current;
            // 路由在事务内按同一份事实重建：任何一条建不出来（凭据来源未分类等）只说明它暂时不可路由，
            // 与启动装配口径一致 —— 它仍在候选表里，只是解析器会拒绝，故障转移到下一个供应商。
            var routes = desired
                .Select(provider => TryBuildProviderRoute(provider, current, secrets))
                .Where(route => route is not null)
                .Cast<ModelProviderRoute>()
                .ToArray();
            transaction.Commit();
            registry.Replace(desired, desired.Select(provider => new ProviderCandidate(provider.Id, provider.Priority, provider.IsEnabled)));
            // 路由表只用于诊断/兜底（真正的请求路由由解析器现取）；这里记一行让"重建成功"可观测。
            FileLog.Write("Model", $"provider_registry_rebuilt providers={desired.Length} routes={routes.Length}");
            return new(ApplyStep.ModelProjection, ApplyState.Applied, null);
        }
        catch (Exception ex)
        {
            FileLog.Warn("Model", $"Provider 注册表热重建失败，保留旧快照；desired/runtime 不一致的请求仍会安全拒绝：{ex.GetType().Name}");
            return new(ApplyStep.ModelProjection, ApplyState.Failed, "model_projection_rebuild_failed");
        }
    }

    private static ModelProviderRoute? TryBuildProviderRoute(
        PersistedModelProvider provider, AppSettings settings, ISecretsRepository secrets)
    {
        if (!provider.IsEnabled) return null;
        var apiKey = ResolveProviderSecret(provider.SecretKeyRef, settings, secrets);
        return BuildProviderRoute(provider, settings, apiKey);
    }

    /// <summary>
    /// 运行期路由与「发现模型」共用的硬边界（#132 S3）。以前测试路由只读库、不比对装配快照，
    /// 于是面板测通、线上拒绝，排障时看不出差异。requireEnabled=false 是测试路由被允许更宽的唯一一处：
    /// 还没启用的供应商也要能试连（保存前先测通再启用）；其余差异（协议 / Options / 非 primary 的端点）
    /// 两条通道必须同样拒绝，拒绝原因写进日志，面板与 // 命令都能看到同一句话。
    /// </summary>
    private static string? ProviderRouteBoundary(PersistedModelProvider desired, PersistedModelProvider? captured,
        bool requireEnabled)
    {
        if (desired.Id.Trim() != desired.Id) return "provider_id_not_trimmed";
        if (requireEnabled && !desired.IsEnabled) return "provider_disabled";
        if (captured is null) return null;
        if (desired.Protocol != captured.Protocol) return "provider_protocol_changed";
        if (desired.Options != captured.Options) return "provider_options_changed";
        if (!string.Equals(desired.Id, "primary", StringComparison.Ordinal)
            && (desired.BaseUrl != captured.BaseUrl || desired.ModelName != captured.ModelName))
            return "provider_endpoint_changed";
        return null;
    }

    private static ModelProviderRoute? TryBuildProviderTestRoute(string providerId, AppSettings current,
        Func<IReadOnlyList<PersistedModelProvider>> captured)
    {
        try
        {
            using var connection = AppDatabase.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var provider = ModelProviderStore.LoadAllInTransaction(connection)
                .FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.Ordinal));
            if (provider is null || ModelProviderStore.ClassifyCredentialBinding(provider.SecretKeyRef)
                is CredentialBindingState.Unclassified) return null;
            var boundary = ProviderRouteBoundary(provider,
                captured().FirstOrDefault(item => string.Equals(item.Id, providerId, StringComparison.Ordinal)),
                requireEnabled: false);
            if (boundary is not null)
            {
                FileLog.Warn("Model", $"provider_test_route_blocked provider={providerId} reason={boundary}");
                return null;
            }
            var key = ResolveProviderSecretInTransaction(provider.SecretKeyRef, current, connection);
            var route = BuildProviderRoute(provider, current, key);
            transaction.Commit();
            return route;
        }
        catch (Exception)
        {
            // A failed coherent observation must not test a stale endpoint or resurrect an old key.
            return null;
        }
    }

    private static ModelProviderRoute? TryBuildCurrentProviderRoute(PersistedModelProvider captured, AppSettings current)
    {
        if (!captured.IsEnabled || captured.Id.Trim() != captured.Id) return null;
        try
        {
            using var connection = AppDatabase.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var desired = ModelProviderStore.LoadAllInTransaction(connection)
                .FirstOrDefault(provider => string.Equals(provider.Id, captured.Id, StringComparison.Ordinal));
            // 只把「装配级」差异当硬边界：协议与选项变了确实要重建客户端。
            // 凭据来源（SecretKeyRef）是请求级材料：面板第一次填 Key 时它从 env:QQCHAT_API_KEY 变成
            // secret:apiKey，如果这里也要求相等，整条路由会被拒 → 所有模型调用进入安全静默（#103 R1）。
            // 端点授权在保存时已经校验过（SettingsStore 的 IsAuthorizedModelEndpoint），运行期只需能解析出密钥。
            if (desired is null)
            {
                FileLog.Write("Model", $"provider_route_blocked provider={captured.Id} reason=provider_missing");
                return null;
            }
            var boundary = ProviderRouteBoundary(desired, captured, requireEnabled: true);
            if (boundary is not null)
            {
                FileLog.Write("Model", $"provider_route_blocked provider={captured.Id} reason={boundary}");
                return null;
            }
            var binding = ModelProviderStore.ClassifyCredentialBinding(desired.SecretKeyRef);
            if (binding == CredentialBindingState.Unclassified
                || binding == CredentialBindingState.Unconfigured && desired.Protocol != ModelProtocol.Ollama) return null;
            var key = ResolveProviderSecretInTransaction(desired.SecretKeyRef, current, connection);
            var route = BuildProviderRoute(captured, current, key);
            transaction.Commit();
            return route;
        }
        catch (Exception)
        {
            // A failed coherent safety observation denies the route; callers must not revive startup caches.
            return null;
        }
    }

    private static string? ResolveProviderSecretInTransaction(string knownRef, AppSettings current,
        Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        if (knownRef == "secret:apiKey")
        {
            var value = StorageSecrets.LoadInTransaction(connection, WritableSecretSlot.ModelApiKey);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        if (knownRef == ModelProviderStore.PrimaryModelCredentialReference) return current.ApiKey;
        if (ModelProviderStore.TryGetRegisteredCredentialKey(knownRef, out var credentialKey))
            return ModelCredentialStore.LoadInTransaction(connection, credentialKey);
        return knownRef == "env:QQCHAT_API_KEY" ? ResolveFixedEnvironmentModelKey(current) : null;
    }

    private static ModelProviderRoute? BuildProviderRoute(PersistedModelProvider provider, AppSettings settings, string? apiKey)
    {
        apiKey = apiKey?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(provider.Protocol)
            || provider.Protocol != ModelProtocol.Ollama && string.IsNullOrWhiteSpace(apiKey)
            || apiKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || apiKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(provider.BaseUrl)
            || string.IsNullOrWhiteSpace(provider.ModelName))
        {
            return null;
        }

        var baseUrl = string.Equals(provider.Id, "primary", StringComparison.Ordinal)
            ? settings.ModelBaseUrl
            : provider.BaseUrl;
        var model = string.Equals(provider.Id, "primary", StringComparison.Ordinal)
            ? settings.Model
            : provider.ModelName;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model)
            || baseUrl.Length > 2048 || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0)
        {
            return null;
        }

        return new ModelProviderRoute(provider.Id, baseUrl.Trim(), model.Trim(), apiKey, provider.Protocol, provider.Options);
    }

    private static string? ResolveProviderSecret(
        string secretRef, AppSettings settings, ISecretsRepository secrets)
    {
        if (string.Equals(secretRef, "secret:apiKey", StringComparison.Ordinal))
        {
            return secrets.Load("apiKey");
        }

        if (secretRef == ModelProviderStore.PrimaryModelCredentialReference) return settings.ApiKey;
        if (ModelProviderStore.TryGetRegisteredCredentialKey(secretRef, out var credentialKey))
            return new ModelCredentialStore().Load(credentialKey);

        if (!string.Equals(secretRef, "env:QQCHAT_API_KEY", StringComparison.Ordinal))
        {
            return null;
        }

        return ResolveFixedEnvironmentModelKey(settings);
    }

    private static string? ResolveFixedEnvironmentModelKey(AppSettings settings)
    {
        var value = BotConfig.FixedModelEnvironmentKey();

        // Preserve the known reference's current settings snapshot fallback, never an arbitrary environment name.
        return string.IsNullOrWhiteSpace(value) ? settings.ApiKey : value;
    }

    private static ProviderCircuitSnapshot ToCircuitSnapshot(PersistedModelProvider provider)
        => new(provider.Id, provider.CircuitState, provider.ConsecutiveHardFailures,
            provider.CooldownUntil, ProbeInFlight: false);

    private static Func<IReadOnlyList<Domain.Ops.CircuitStatusSnapshot>> BuildCircuitStatusProvider(
        Domain.Ports.IModelClient brain,
        ToolCircuitBreaker ttsBreaker,
        ToolCircuitBreaker searchBreaker,
        ProtocolRiskBackoff riskBackoff) => () =>
    {
        var list = new List<Domain.Ops.CircuitStatusSnapshot>();
        list.AddRange(brain.CircuitSnapshots);
        list.Add(new Domain.Ops.CircuitStatusSnapshot("tool", "tts", ttsBreaker.Snapshot().State.ToString().ToLowerInvariant()));
        list.Add(new Domain.Ops.CircuitStatusSnapshot("tool", "search", searchBreaker.Snapshot().State.ToString().ToLowerInvariant()));
        list.Add(new Domain.Ops.CircuitStatusSnapshot("protocol", "onebot", riskBackoff.HasAnyActive() ? "open" : "closed"));
        return list;
    };

    /// <summary>
    /// ③ 装配阶段三：模型客户端与媒体件（#110 第 2 步拆出）。
    ///
    /// 先建两个工具熔断器（tts/search），再交给 <see cref="BuildModelAndMediaLayer"/> ——
    /// 它们要在同一层里被装进 OpenAiClient，晚建就会让第一批调用没有熔断记账。
    /// </summary>
    private static void AssembleModel(Assembly a)
    {
        // ② 模型与媒体层（模型客户端 / 表情包 / 桥 / 语音 / 音乐与链接 / 联网研究）：见 BuildModelAndMediaLayer
        a.TtsBreaker = new ToolCircuitBreaker("tts");
        a.SearchBreaker = new ToolCircuitBreaker("search");
        var (brain, store, profiles, stickers, voice, music, links, research) =
            BuildModelAndMediaLayer(a.Settings, a.Box, a.Source, a.Secrets, a.ProviderStore, a.ModelRegistry,
                a.TtsBreaker, a.SearchBreaker);
        a.Brain = brain;
        a.Store = store;
        a.Profiles = profiles;
        a.Stickers = stickers;
        a.Voice = voice;
        a.Music = music;
        a.Links = links;
        a.Research = research;
    }
}
