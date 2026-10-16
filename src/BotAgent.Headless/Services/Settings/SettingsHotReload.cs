extern alias StorageModule;

using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using ConfigurationModule = StorageModule::BotAgent.Storage;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using BotAgent.Services.Ports;
using BotAgent.Domain.Ports;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Settings;

/// <summary>
/// 配置热更新（§6.2 的 SettingsHotReload，批次 5 从 <c>BotAgentHost</c> 搬出来）：
/// 面板保存一次设置之后，把"跟着配置走的那堆派生物"按固定顺序重建一遍 ——
/// 能力策略 → 参与上限 → 白名单（含清理）→ agent 用户名单 → 推给大脑 → 并发闸门 →
/// 会话窗口裁剪 → 定时器重排，最后落盘 + 通知面板。
///
/// 为什么单独一个类：面板想"保存设置"以前必须先拿到 <c>BotAgentHost</c>（那是它作为 façade 的副作用之一），
/// 而这段编排要的十几个依赖又把 <c>BotAgentHost</c> 的字段撑得看不见主线。搬到这里之后两边都松开了：
/// 面板只对着"配置这件事"，<c>BotAgentHost</c> 只对着"一次回复这件事"。
///
/// **顺序是语义**（先重建再裁剪、先推大脑再换闸门），所以整段一起搬、一条都没拆散；
/// 唯一的减法见 <see cref="SyncBrainFields" /> 的注释（图片重签那条通道本来就不随配置变）。
/// </summary>
public sealed class SettingsHotReload
{
    private readonly SettingsBox _box;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly WhitelistGate _whitelist;
    private readonly AgentCommandService _agentCmds;
    private readonly IModelClient _brain;
    private readonly ReplyPipeline _reply;
    private readonly BotScheduler _scheduler;
    private readonly ConversationRegistry _registry;
    private readonly PanelNotifier _ui;
    private readonly ISettingsRepository _settingsRepo;
    private ConfigurationModule.ConfigurationStore? _configuration;
    private ConfigurationRequestAuthorization? _configurationAuthorization;
    private Func<ConfigurationVersion, AppSettings, ConfigurationPatch, ImmutableArray<ConfigurationModule.SecretMutation>, AppSettings>? _readCommitted;
    private Func<ConfigurationCaller, ConfigurationOperationKey, FieldEdit<string>, ConfigurationModule.LegacyActionResult>? _applyLegacyMood;
    private Func<ConfigurationFieldId, FieldEffect>? _fieldEffect;
    private Action? _reapplyHealthReports;
    private Func<ConfigurationVersion, AppSettings, ApplyStepReport>? _projectTts;
    private Func<ConfigurationVersion, ApplyStepReport>? _modelProjectionState;

    internal sealed class ConfigurationRequestAuthorization
    {
        private sealed class Request(ConfigurationCaller caller, Func<bool> authorize)
        {
            internal readonly ConfigurationCaller Caller = caller;
            internal readonly Func<bool> Authorize = authorize;
            internal int Active = 1;
        }
        private sealed class Lease(ConfigurationRequestAuthorization owner, Request request, Request? previous) : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                Volatile.Write(ref request.Active, 0);
                if (ReferenceEquals(owner._request.Value, request))
                    owner._request.Value = previous is not null && Volatile.Read(ref previous.Active) == 1 ? previous : null;
            }
        }
        private readonly AsyncLocal<Request?> _request = new();
        internal IDisposable Enter(ConfigurationCaller caller, Func<bool> reauthorize)
        {
            ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(reauthorize);
            var previous = _request.Value; var request = new Request(caller, reauthorize);
            _request.Value = request; return new Lease(this, request, previous);
        }
        internal bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required)
        {
            var request = _request.Value;
            if (request is null || Volatile.Read(ref request.Active) != 1 || request.Caller != caller || (caller.Access & required) != required) return false;
            try { return request.Authorize(); } catch (Exception) { return false; }
        }
        internal bool TryGetCaller(ConfigurationAccess required, out ConfigurationCaller caller)
        {
            caller = _request.Value?.Caller!;
            return caller is not null && Reauthorize(caller, required);
        }
    }

    internal void BindConfiguration(ConfigurationModule.ConfigurationStore store, ConfigurationRequestAuthorization authorization,
        Func<ConfigurationVersion, AppSettings, ConfigurationPatch, ImmutableArray<ConfigurationModule.SecretMutation>, AppSettings> readCommittedSnapshot,
        Func<ConfigurationCaller, ConfigurationOperationKey, FieldEdit<string>, ConfigurationModule.LegacyActionResult> applyLegacyMood,
        Action reapplyHealthReports, Func<ConfigurationVersion, AppSettings, ApplyStepReport> projectTts,
        Func<ConfigurationFieldId, FieldEffect> fieldEffect, Func<ConfigurationVersion, ApplyStepReport> modelProjectionState)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(readCommittedSnapshot); ArgumentNullException.ThrowIfNull(applyLegacyMood);
        ArgumentNullException.ThrowIfNull(reapplyHealthReports); ArgumentNullException.ThrowIfNull(projectTts);
        ArgumentNullException.ThrowIfNull(fieldEffect); ArgumentNullException.ThrowIfNull(modelProjectionState);
        if (_configuration is not null)
        {
            if (ReferenceEquals(_configuration, store) && ReferenceEquals(_configurationAuthorization, authorization)
                && ReferenceEquals(_readCommitted, readCommittedSnapshot) && ReferenceEquals(_applyLegacyMood, applyLegacyMood)
                && ReferenceEquals(_reapplyHealthReports, reapplyHealthReports) && ReferenceEquals(_projectTts, projectTts)
                && ReferenceEquals(_fieldEffect, fieldEffect) && ReferenceEquals(_modelProjectionState, modelProjectionState)) return;
            throw new InvalidOperationException("configuration_already_bound");
        }
        _configurationAuthorization = authorization; _readCommitted = readCommittedSnapshot; _applyLegacyMood = applyLegacyMood;
        _fieldEffect = fieldEffect; _modelProjectionState = modelProjectionState;
        _reapplyHealthReports = reapplyHealthReports; _projectTts = projectTts; _configuration = store;
    }
    internal ConfigurationRequestAuthorization ConfigurationAuthorization
        => _configurationAuthorization ?? throw new InvalidOperationException("configuration_not_bound");

    internal ValueTask<ConfigurationReadResult> ReadConfigurationAsync(ConfigurationReadKind kind, CancellationToken ct = default)
    {
        if (_configuration is null || !ConfigurationAuthorization.TryGetCaller(kind == ConfigurationReadKind.Editable
            ? ConfigurationAccess.ReadEditable : ConfigurationAccess.ReadOverview, out var caller))
            return ValueTask.FromResult(new ConfigurationReadResult(ConfigurationResultCode.Forbidden, default, default,
                "cfg-0.3.2", 0, null, ImmutableArray<SecretMetadata>.Empty, ImmutableArray<FieldDescriptor>.Empty, ApplyState.Unknown));
        return _configuration.ReadAsync(caller, kind, ct);
    }
    internal ValueTask<ConfigurationModule.LegacyFindOutcome> FindConfigurationAsync(ConfigurationOperationKey key, CancellationToken ct = default)
    {
        if (_configuration is null || !ConfigurationAuthorization.TryGetCaller(ConfigurationAccess.SaveConfiguration, out var caller))
            return ValueTask.FromResult(new ConfigurationModule.LegacyFindOutcome(new(ConfigurationResultCode.Forbidden, null),
                ImmutableArray<ConfigurationModule.LegacyActionResult>.Empty));
        return ConfigurationBridge.FindLegacyAsync(_configuration, caller, key, ct);
    }
    internal ValueTask<ConfigurationSaveResult> SaveConfigurationAsync(ConfigurationSaveRequest request, CancellationToken ct = default)
    {
        if (_configuration is null || !ConfigurationAuthorization.TryGetCaller(ConfigurationAccess.SaveConfiguration, out var caller))
            return ValueTask.FromResult(ConfigurationRejected(ConfigurationResultCode.Forbidden));
        var result = SaveAndApply(caller, request.Key, request.Changes, request.Secrets,
            () => _configuration.SaveAsync(caller, request, ct).GetAwaiter().GetResult(),
            TtsRequested(request.Changes, request.Secrets));
        return ValueTask.FromResult(result);
    }
    internal ValueTask<ConfigurationModule.LegacySaveOutcome> SaveLegacyConfigurationAsync(ConfigurationOperationKey? key,
        ConfigurationPatch changes, ImmutableArray<SecretEdit> secrets, FieldEdit<string> mood,
        bool ttsProjectionRequested, CancellationToken ct = default)
    {
        var empty = ImmutableArray<ConfigurationModule.LegacyActionResult>.Empty;
        if (_configuration is null || !ConfigurationAuthorization.TryGetCaller(ConfigurationAccess.SaveConfiguration, out var caller))
            return ValueTask.FromResult(new ConfigurationModule.LegacySaveOutcome(ConfigurationRejected(ConfigurationResultCode.Forbidden), empty));
        if (key is null)
        {
            var header = _configuration.ReadAsync(caller, ConfigurationReadKind.Overview, ct).GetAwaiter().GetResult();
            if (header.Generation.Value == Guid.Empty)
                return ValueTask.FromResult(new ConfigurationModule.LegacySaveOutcome(ConfigurationRejected(header.Code), empty));
            key = new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid());
        }
        var actualKey = key.Value;
        ConfigurationModule.LegacySaveOutcome? outcome = null;
        void ApplyMood()
        {
            if (mood.Kind == EditKind.Keep || outcome is null || outcome.ConfigurationResult.Replayed) return;
            var action = _applyLegacyMood!(caller, actualKey, mood);
            outcome = outcome with { LegacyActionResults = ImmutableArray.Create(action) };
        }
        var result = SaveAndApply(caller, actualKey, changes, secrets, () =>
        {
            outcome = ConfigurationBridge.SaveLegacyWithMoodAsync(_configuration, caller, actualKey, changes, secrets, mood, ct)
                .GetAwaiter().GetResult();
            return outcome.ConfigurationResult;
        }, ttsProjectionRequested, ApplyMood);
        return ValueTask.FromResult((outcome ?? new(result, empty)) with { ConfigurationResult = result });
    }
    private static ConfigurationSaveResult ConfigurationRejected(ConfigurationResultCode code)
        => new(code, false, null, default, ImmutableArray<FieldError>.Empty);
    private static bool FirstChanged(ConfigurationSaveResult result)
        => !result.Replayed && result.Code == ConfigurationResultCode.Ok
            && result.Receipt is { Decision: ConfigurationDecision.Changed, CommitState: CommitState.Committed };
    private static bool TtsRequested(ConfigurationPatch patch, ImmutableArray<SecretEdit> secrets)
        => secrets.Any(edit => edit.Slot == WritableSecretSlot.TtsApiKey && edit.Kind != SecretEditKind.Keep)
            || patch.Voice is { } voice && (voice.TtsProvider.Kind != EditKind.Keep
                || voice.TtsApiBase.Kind != EditKind.Keep || voice.TtsModel.Kind != EditKind.Keep);
    private bool RequiresRestart(ConfigurationPatch patch, ImmutableArray<SecretEdit> secrets)
    {
        if (secrets.Any(edit => edit.Kind != SecretEditKind.Keep && edit.Slot is WritableSecretSlot.FeishuAppSecret
            or WritableSecretSlot.FeishuEncryptKey or WritableSecretSlot.FeishuVerificationToken)) return true;
        if (patch.Registered is { } registered && (!registered.Plugins.IsEmpty || registered.Providers.Any(edit =>
            edit.Key.Value != "primary" || edit.Changes.NameRaw.Kind != EditKind.Keep
            || edit.Changes.Priority.Kind != EditKind.Keep || edit.Changes.Enabled.Kind != EditKind.Keep
            || edit.Changes.Credential.Kind != CredentialBindingEditKind.KeepExisting))) return true;
        var node = System.Text.Json.JsonSerializer.SerializeToNode(patch)!.AsObject();
        foreach (var partition in node.Where(pair => pair.Key != nameof(ConfigurationPatch.Registered)))
        {
            if (partition.Value is not System.Text.Json.Nodes.JsonObject edits) continue;
            foreach (var edit in edits)
                if (Enum.TryParse<ConfigurationFieldId>(edit.Key, out var field)
                    && _fieldEffect!(field) == FieldEffect.Restart
                    && edit.Value?["kind"]?.GetValue<int>() != (int)EditKind.Keep) return true;
        }
        return false;
    }
    private ConfigurationSaveResult SaveAndApply(ConfigurationCaller caller, ConfigurationOperationKey key,
        ConfigurationPatch changes, ImmutableArray<SecretEdit> secrets, Func<ConfigurationSaveResult> persist,
        bool projectTts, Action? applyMood = null)
    {
        var mutations = Enum.GetValues<WritableSecretSlot>().Select(slot => new ConfigurationModule.SecretMutation(slot,
            secrets.FirstOrDefault(edit => edit.Slot == slot)?.Kind ?? SecretEditKind.Keep)).ToImmutableArray();
        return _box.ApplyConfiguration(previous =>
        {
            var saved = persist();
            if (!saved.Replayed && saved.Code == ConfigurationResultCode.NoChanges
                && saved.Receipt is { Decision: ConfigurationDecision.NoChanges, CommitState: CommitState.NotCommitted })
                applyMood?.Invoke();
            if (!FirstChanged(saved)) return (previous, saved, false);
            AppSettings next;
            try { next = _readCommitted!(saved.Receipt!.CommittedVersion!.Value, previous, changes, mutations); }
            catch (Exception) { return (previous, saved with { Code = ConfigurationResultCode.StorageUnavailable }, false); }
            foreach (var step in new[] { ApplyStep.PublishSnapshot, ApplyStep.RuntimePolicy, ApplyStep.ModelProjection })
            {
                var claim = ConfigurationBridge.TryStartApplyStep(_configuration!, caller, key, step);
                if (!claim.CanRun)
                    return (previous, saved with { Code = claim.Result.Code, Receipt = claim.Result.Receipt ?? saved.Receipt }, false);
                saved = saved with { Receipt = claim.Result.Receipt ?? saved.Receipt };
            }
            return (next, saved, true);
        }, (next, saved) =>
        {
            bool Report(ApplyStepReport report)
            {
                var recorded = _configuration!.RecordApply(caller, key, ImmutableArray.Create(report));
                saved = saved with { Code = recorded.Code, Receipt = recorded.Receipt ?? saved.Receipt };
                return recorded.Code == ConfigurationResultCode.Ok;
            }
            bool Run(ApplyStep step, Action action, bool claimed = false)
            {
                if (!claimed)
                {
                    var claim = ConfigurationBridge.TryStartApplyStep(_configuration!, caller, key, step);
                    saved = saved with { Code = claim.Result.Code, Receipt = claim.Result.Receipt ?? saved.Receipt };
                    if (!claim.CanRun) return false;
                }
                try { action(); return Report(new(step, ApplyState.Applied, null)); }
                catch (Exception) { Report(new(step, ApplyState.Failed, "apply_failed")); return false; }
            }
            if (!Report(new(ApplyStep.PublishSnapshot, ApplyState.Applied, null))) return saved;
            if (!Run(ApplyStep.RuntimePolicy, () =>
            {
                _approvals.RebuildPolicy(); _participation.Rebuild(); _whitelist.Rebuild();
                PruneNonWhitelisted(); _agentCmds.RebuildAgentUsers();
            }, true)) return saved;
            ApplyStepReport modelReport;
            try
            {
                modelReport = _modelProjectionState!(saved.Receipt!.CommittedVersion!.Value);
                if (modelReport.Step != ApplyStep.ModelProjection
                    || modelReport.State is not (ApplyState.Applied or ApplyState.RestartRequired))
                    throw new InvalidOperationException("model_projection_state_invalid");
                SyncBrainFields();
            }
            catch (Exception) { Report(new(ApplyStep.ModelProjection, ApplyState.Unknown, "projection_unavailable")); return saved; }
            if (!Report(modelReport)) return saved;
            if (!Run(ApplyStep.ConcurrencyGate, () => _reply.ResizeGate(Math.Clamp(next.MaxConcurrentReplies, 1, 16)))) return saved;
            if (!Run(ApplyStep.ContextWindow, () =>
            {
                foreach (var conversation in _registry.Snapshot())
                { conversation.MaxMessages = next.MaxMessagesPerConversation; conversation.TrimToMax(); }
            })) return saved;
            if (!Run(ApplyStep.Schedulers, () => { _scheduler.RebuildIfNeeded(); _reapplyHealthReports!(); })) return saved;
            foreach (var step in new[] { ApplyStep.TtsFileProjection, ApplyStep.RegisteredConfiguration })
            {
                var claim = ConfigurationBridge.TryStartApplyStep(_configuration!, caller, key, step);
                saved = saved with { Code = claim.Result.Code, Receipt = claim.Result.Receipt ?? saved.Receipt };
                if (!claim.CanRun) return saved;
                var report = new ApplyStepReport(step, ApplyState.Applied, null);
                if (step == ApplyStep.TtsFileProjection && projectTts)
                {
                    try { report = _projectTts!(saved.Receipt!.CommittedVersion!.Value, next); }
                    catch (Exception) { report = new(step, ApplyState.Unknown, "projection_unavailable"); }
                }
                if (step == ApplyStep.RegisteredConfiguration
                    && (modelReport.State == ApplyState.RestartRequired || RequiresRestart(changes, secrets)))
                    report = new(step, ApplyState.RestartRequired, "restart_required");
                if (!Report(report)) return saved;
            }
            applyMood?.Invoke();
            _ui.EmitLog("配置已更新"); _ui.NotifyConversationsChanged(); _ui.NotifyStateChanged();
            return saved;
        });
    }


    public SettingsHotReload(
        SettingsBox box,
        ISettingsRepository settingsRepo,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        WhitelistGate whitelist,
        AgentCommandService agentCmds,
        IModelClient brain,
        ReplyPipeline reply,
        BotScheduler scheduler,
        ConversationRegistry registry,
        PanelNotifier ui)
    {
        _box = box;
        _settingsRepo = settingsRepo;
        _approvals = approvals;
        _participation = participation;
        _whitelist = whitelist;
        _agentCmds = agentCmds;
        _brain = brain;
        _reply = reply;
        _scheduler = scheduler;
        _registry = registry;
        _ui = ui;
    }

    /// <summary>配置读取入口：指向**当前发布版**（热更新是换引用，见 <see cref="SettingsBox" />）。</summary>
    private AppSettings _settings => _box.Current;

    /// <summary>
    /// 保存成功后发布设置并重建白名单/人设/阈值/限流等运行时派生状态。
    /// 保存失败保持旧版本；提交后重建失败会抛出，但不伪造数据库回滚。
    /// 模型地址与 OneBot 地址不在运行时生效（属于容器环境变量职责），由 UI 明确标注。
    /// </summary>
    public void ApplyRuntimeSettings(Action<AppSettings> mutate, AuditEvent? auditEvent = null,
        IAuditChain? auditChain = null, Action<AppSettings>? published = null)
    {
        if (_configuration is not null)
        {
            if (published is not null) throw new InvalidOperationException("configuration_typed_writer_required");
            var previous = _box.Current; var candidate = previous.Snapshot(); mutate(candidate);
            var before = System.Text.Json.JsonSerializer.SerializeToNode(previous)!.AsObject();
            var after = System.Text.Json.JsonSerializer.SerializeToNode(candidate)!.AsObject();
            before.Remove(nameof(AppSettings.PanelDeployUrl)); after.Remove(nameof(AppSettings.PanelDeployUrl));
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(before, after))
                throw new InvalidOperationException("configuration_typed_writer_required");
            var caller = new ConfigurationCaller("headless", "panel-deploy", "bot", ConfigurationAccess.SaveConfiguration | ConfigurationAccess.ReadOverview);
            using var scope = ConfigurationAuthorization.Enter(caller, () => true);
            var result = SaveLegacyConfigurationAsync(null, new(null, null, new AgentChanges
                { PanelDeployUrl = FieldEdit<string>.Set(candidate.PanelDeployUrl) }, null, null, null, null, null),
                ImmutableArray<SecretEdit>.Empty, FieldEdit<string>.Keep(), false).GetAwaiter().GetResult().ConfigurationResult;
            if (result.Code is not (ConfigurationResultCode.Ok or ConfigurationResultCode.NoChanges))
                throw new InvalidOperationException("configuration_save_failed");
            return;
        }
        if (ConfigurationBridge.RequiresCoordinatedConfigurationWrites)
            throw new InvalidOperationException("configuration_not_bound");
        _box.ApplyPersisted(mutate,
            next => _settingsRepo.Save(next, auditEvent, auditChain),
            next =>
            {
                RebuildRuntime();
                published?.Invoke(next);
            });
    }

    private void RebuildRuntime()
    {
        // 能力策略快照：改完设置立刻按新开关重建（正在处理的那一轮仍用旧快照，V3 §5.3）
        _approvals.RebuildPolicy();

        // 参与状态机的上下限同样跟随设置（状态保留、只换上限）
        _participation.Rebuild();

        // 白名单重新解析并清理已有会话
        _whitelist.Rebuild();
        PruneNonWhitelisted();

        // Agent 用户白名单（改设置后立即生效；空 = 谁都不能用）
        _agentCmds.RebuildAgentUsers();

        // 同步到模型客户端
        SyncBrainFields();

        // 全局并发数变更时换闸门（旧的不 Dispose：在途的那次还在等它，见 ReplyPipeline.ResizeGate）
        _reply.ResizeGate(Math.Clamp(_settings.MaxConcurrentReplies, 1, 16));

        // 消息窗口变更时同步到已有会话，并立即裁一次（否则要等下一条消息才看得到效果）
        foreach (var c in _registry.Snapshot())
        {
            c.MaxMessages = _settings.MaxMessagesPerConversation;
            c.TrimToMax();
        }

        // 定时器类配置（静默兜底 / 画像巡检）：只在真的改了时才重建，否则不生效
        _scheduler.RebuildIfNeeded();

        _ui.EmitLog("配置已更新");
        _ui.NotifyConversationsChanged();
        _ui.NotifyStateChanged();
    }

    /// <summary>
    /// 只把「当前生效的那一份」里的某个值换掉：**不重建派生物、不落盘**。
    /// 用在"已经在别处持久化过"的运行时值上 —— 例如网易云登录态（存 secrets 表、<c>[JsonIgnore]</c> 不进 settings.json）：
    /// 它既不该触发白名单清理，也不该让面板多出一条"配置已更新"。
    /// </summary>
    public void PatchSettings(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        _box.Apply(next =>
        {
            // The only current caller is the non-C3 music cookie projection. Never publish arbitrary user edits.
            var candidate = next.Snapshot(); mutate(candidate);
            if (!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.JsonSerializer.SerializeToNode(next),
                System.Text.Json.JsonSerializer.SerializeToNode(candidate)))
                throw new InvalidOperationException("configuration_typed_writer_required");
            next.NeteaseCookie = candidate.NeteaseCookie;
        });
    }

    /// <summary>
    /// 会话被删除 / 移出白名单时，把它在各台账里的痕迹一起清掉（V3 §7.2 / §9.4：内存台账要有界）。
    /// 审批台账按“已结束的单子”周期清理（<see cref="ApprovalStore.Prune" />），不在这里逐会话删 ——
    /// 审批单可能跨越一次白名单变更，删早了会让正在等待批准的那条悄悄失效。
    /// </summary>
    public void ForgetSessionState(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        _participation.Forget(sourceKey);
        _approvals.Forget(sourceKey);
    }

    /// <summary>把当前发布版里与模型相关的字段推给大脑（身份 / 人设 / 欲望 / 阈值 / 上下文窗口）。</summary>
    private void SyncBrainFields()
    {
        _brain.BotIdentity = string.IsNullOrWhiteSpace(_settings.NormalizedUin) ? null : _settings.NormalizedUin;
        _brain.BotPersona = _settings.BotPersona;
        _brain.AiDesire = _settings.AiDesire;
        _brain.SuitabilityThreshold = _settings.SuitabilityThreshold;
        _brain.MaxContextMessages = _settings.MaxContextMessages;

        // 图片地址过期（QQ 的 rkey 有时效）时让协议端重新签发 —— 那条通道**不随配置变**，
        // 已经在装配点接过一次（CompositionRoot：brain.RefreshImageUrls = …），所以这里不再重复接线。
        // （以前两处都写了一遍，那是搬代码时留下的重影，不是有意的双保险。）
    }

    /// <summary>
    /// 白名单变更后收尾。
    ///
    /// ⚠ 2026-09-21 修语义：白名单只管“要不要回”，**不管“存不存”**。
    /// 以前这里会把不在白名单里的会话从内存里删掉 ✗；随后 <c>Save()</c> 重写 conversations 表时
    /// 那些行就没了 ✗（messages 表的行还在 ✗）—— 后果就是**面板里那个会话直接消失
    /// 或打开是空的**（管理员报的“群聊会话不显示”就是这个 ✗）。
    /// 现在只把“不在白名单”的会话列出来，用于清在途的待回复项；
    /// 会话本体与历史**一律保留**（不在白名单的入站在 HandleInbound 那一道就被拦了，不会污染上下文）。
    /// </summary>
    private void PruneNonWhitelisted()
    {
        var removed = _registry.Snapshot().Where(c => !_whitelist.AllowsKey(c.SourceKey)).ToList();

        foreach (var c in removed)
        {
            _reply.ForgetSession(c.SourceKey);
            ForgetSessionState(c.SourceKey);
        }

        if (removed.Count > 0)
        {
            _registry.Save();
        }
    }
}
