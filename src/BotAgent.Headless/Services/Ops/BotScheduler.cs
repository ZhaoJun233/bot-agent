using System.Collections.Concurrent;
using System.Text.Json;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Services.Agent;
using BotAgent.Services.Memory;
using BotAgent.Services.OneBot;
using BotAgent.Services.Panel;
using BotAgent.Services.Plugins;
using BotAgent.Services.Profiles;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;

namespace BotAgent.Services.Ops;

/// <summary>
/// 后台巡检（宿主只保留静默兜底与 QQ 账号在线探测；画像巡检定时器与执行归 <c>preset.feature.profiles</c>、
/// 表情包巡检定时器与执行归 <c>preset.feature.stickers</c>）。
/// </summary>
public sealed class BotScheduler
{
    public const string ProfilesProviderId = "preset.feature.profiles";
    private const int ReplyMaintenanceSeconds = 60;
    private readonly SettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IProfileRepository _profiles;
    private readonly IModelClient _brain;
    private readonly ReplyPipeline _reply;
    private readonly StickerService _stickers;
    private readonly PanelNotifier _ui;
    private readonly BotSchedulerHooks _hooks;

    private Timer? _idleTimer;
    private Timer? _healthTimer;
    private int _healthRunning;
    private int _accountOnline = -1;

    private int _timerIdleSeconds = -1;
    private bool _timerSummaryEnabled;
    private int _timerSummarySeconds = -1;
    private bool _timerStickersEnabled;
    private string? _timerProfilesGeneration;
    private string? _timerStickersGeneration;
    private bool _timerAttestedMode;

    private readonly Func<string, bool>? _pluginAllows;
    private readonly IUnifiedMemory? _unifiedMemory;
    private readonly PluginManager? _plugins;
    private readonly Func<bool>? _allowAttestedSummary;
    private readonly AttestedProfileProvenanceRegistry? _profileProvenance;
    private IProfilesRuntimeContribution? _directProfiles;

    public BotScheduler(
        SettingsBox box,
        IQqChatSource source,
        IProfileRepository profiles,
        OpenAiClient brain,
        ReplyPipeline reply,
        StickerService stickers,
        PanelNotifier ui,
        BotSchedulerHooks hooks,
        Func<string, bool>? pluginAllows = null,
        IUnifiedMemory? unifiedMemory = null,
        PluginManager? plugins = null,
        IProfilesRuntimeContribution? directProfiles = null,
        Func<bool>? allowAttestedSummary = null,
        AttestedProfileProvenanceRegistry? profileProvenance = null)
    {
        _box = box;
        _source = source;
        _profiles = profiles;
        _brain = brain;
        _reply = reply;
        _stickers = stickers;
        _ui = ui;
        _hooks = hooks;
        _pluginAllows = pluginAllows;
        _unifiedMemory = unifiedMemory;
        _plugins = plugins;
        _directProfiles = directProfiles;
        _allowAttestedSummary = allowAttestedSummary;
        _profileProvenance = profileProvenance;
    }

    private AppSettings _settings => _box.Current;

    private bool AttestedSummaryMode => _unifiedMemory is not null
        && _allowAttestedSummary?.Invoke() == true
        && _profileProvenance is not null;

    /// <summary>仅供无插件宿主的独立单测注入包内画像运行时实现。</summary>
    public void BindDirectProfiles(IProfilesRuntimeContribution runtime) => _directProfiles = runtime;

    /// <summary>供宿主装配层注册到插件服务容器，向 <c>preset.feature.profiles</c> 提供窄宿主端口。</summary>
    public IProfilesHostPorts CreateProfilesHostPorts() => new ProfilesHostPorts(this);

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private IProfilesRuntimeContribution? CurrentProfilesRuntime
        => _directProfiles ?? (_plugins is not null && _plugins.TryGetContribution<IProfilesRuntimeContribution>(ProfilesProviderId, out var runtime)
            ? runtime : null);

    // The legacy background queue has no attested source or revocable use receipt.
    // Scoped profile mode therefore keeps trusted fragments and only allows model summary
    // when attested provenance + allowAttestedSummary toggle are both explicitly active.
    private bool ProfileSummarizationAllowed() => (_unifiedMemory is null || AttestedSummaryMode)
        && _settings.EnableProfileSummary && !_hooks.IsDisposed()
        && (_pluginAllows?.Invoke(PluginGate.ProfilesCapability) ?? true);

    /// <summary>QQ 账号在线状态：null = 还不知道（协议端没答复过）。</summary>
    public bool? AccountOnline
        => Volatile.Read(ref _accountOnline) is var v && v >= 0 ? v == 1 : null;

    /// <summary>画像巡检：成功 / 失败累计（由 <c>preset.feature.profiles</c> 独立包持有）。</summary>
    public long SummaryDoneCount
    {
        get
        {
            try { return CurrentProfilesRuntime?.SummaryDoneCount ?? 0; }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return 0; }
        }
    }

    public long SummaryFailCount
    {
        get
        {
            try { return CurrentProfilesRuntime?.SummaryFailCount ?? 0; }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return 0; }
        }
    }

    /// <summary>连接断了 → 账号在线状态回到未知（重连后再探一次才知道）。</summary>
    public void ResetAccountState() => Interlocked.Exchange(ref _accountOnline, -1);

    /// <summary>启动：建定时器 + 起账号在线探测（连接建立后会立即先探一次）。</summary>
    public void Start()
    {
        RebuildIfNeeded();

        // QQ 账号在线探测：30 秒一轮（连接建立后会立即先探一次）
        _healthTimer = new Timer(
            _ => _ = CheckAccountOnlineAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(30));
    }

    /// <summary>收摊：停掉所有定时器（在途那一轮自然结束）。</summary>
    public void Stop()
    {
        _idleTimer?.Dispose();
        _healthTimer?.Dispose();
        _idleTimer = null;
        _healthTimer = null;
        try { CurrentProfilesRuntime?.StopSummaryTimer(); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    /// <summary>设置变了 → 只在相关项真的变了时才重建定时器（否则每次保存都会重置计时）。</summary>
    public void RebuildIfNeeded()
    {
        var idleSeconds = ReplyMaintenanceSeconds;
        var summaryEnabled = ProfileSummarizationAllowed();
        var summarySeconds = Math.Max(0, _settings.ProfileSummaryIntervalSeconds);
        var stickersEnabled = _settings.EnableStickers && _settings.StickerLibraryMax > 0
            && (_pluginAllows?.Invoke(PluginGate.StickersCapability) ?? true);
        var profilesGeneration = _plugins?.GetGenerationId(ProfilesProviderId);
        var stickersGeneration = _plugins?.GetGenerationId(StickerService.ProviderId);
        var attestedMode = AttestedSummaryMode;

        if (idleSeconds == _timerIdleSeconds &&
            summaryEnabled == _timerSummaryEnabled &&
            summarySeconds == _timerSummarySeconds &&
            stickersEnabled == _timerStickersEnabled &&
            attestedMode == _timerAttestedMode &&
            string.Equals(profilesGeneration, _timerProfilesGeneration, StringComparison.Ordinal) &&
            string.Equals(stickersGeneration, _timerStickersGeneration, StringComparison.Ordinal))
        {
            return;
        }

        _timerIdleSeconds = idleSeconds;
        _timerSummaryEnabled = summaryEnabled;
        _timerSummarySeconds = summarySeconds;
        _timerStickersEnabled = stickersEnabled;
        _timerAttestedMode = attestedMode;
        _timerProfilesGeneration = profilesGeneration;
        _timerStickersGeneration = stickersGeneration;

        // ---- 回复巡检；节奏等待由协调器自己的计时器负责 ----
        _idleTimer?.Dispose();
        _idleTimer = null;
        if (idleSeconds > 0 && !_hooks.IsDisposed())
        {
            _idleTimer = new Timer(
                _ => _reply.IdleFallbackTick(),
                null,
                TimeSpan.FromSeconds(idleSeconds),
                TimeSpan.FromSeconds(idleSeconds));
        }

        // ---- 长期记忆：画像巡检（定时器归 preset.feature.profiles 独立包管理）----
        try { CurrentProfilesRuntime?.EnsureSummaryTimer(summaryEnabled, summarySeconds); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }

        // ---- 表情包：补说明 + 自巡检（定时器归 preset.feature.stickers 独立包管理）----
        _stickers.EnsureTimer(stickersEnabled);

        _hooks.Log($"定时器已按新配置重建（回复巡检 {idleSeconds}s，画像巡检 {(summaryEnabled && summarySeconds > 0 ? summarySeconds + "s" : "关")}，" +
                   $"表情包巡检 {(stickersEnabled && _settings.StickerCurateIntervalSeconds > 0 ? _settings.StickerCurateIntervalSeconds + "s" : "关")}）");
    }

    /// <summary>
    /// QQ 账号在线探测（协议端 <c>get_login_info</c> 那类动作）：掉线要只报一次，恢复也报一次。
    /// </summary>
    public async Task CheckAccountOnlineAsync()
    {
        var gateway = _source as OneBotGateway
            ?? (_source as IChannelRegistry)?.Get(Channels.Private) as OneBotGateway;
        if (_hooks.IsDisposed() || gateway is null || !gateway.IsConnected)
        {
            return;
        }

        if (Interlocked.Exchange(ref _healthRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var online = await gateway.GetAccountOnlineAsync();
            if (online is null)
            {
                return;
            }

            var next = online.Value ? 1 : 0;
            var previous = Interlocked.Exchange(ref _accountOnline, next);
            if (previous == next)
            {
                return;
            }

            if (next == 0)
            {
                _hooks.Log("⚠ QQ 账号已离线（登录失效或被顶号）：从现在起收不到任何消息。" +
                           "请打开 NapCat 管理面板重新扫码登录（面板地址见部署目录 README）。");
            }
            else
            {
                _hooks.Log("QQ 账号已上线，恢复正常接收消息。");
            }

            _ui.NotifyStateChanged();
        }
        catch
        {
            // 探测失败不打扰用户：下一轮自然会重试
        }
        finally
        {
            Interlocked.Exchange(ref _healthRunning, 0);
        }
    }

    private sealed class ProfilesHostPorts(BotScheduler owner) : IProfilesHostPorts
    {
        private readonly ConcurrentDictionary<string, (UnifiedMemoryReceipt Receipt, UnifiedMemoryContext Context, string[] Snippets)> _pendingReceipts = new(StringComparer.Ordinal);

        public ProfileSummarySettingsSnapshot ReadSummarySettings()
        {
            var s = owner._settings;
            return new(
                EnableProfileSummary: s.EnableProfileSummary,
                IntervalSeconds: Math.Max(0, s.ProfileSummaryIntervalSeconds),
                Threshold: s.ProfileSummaryThreshold,
                MaxChars: s.ProfileSummaryMaxChars,
                LegacySummaryDisabled: owner._unifiedMemory is not null && !owner.AttestedSummaryMode,
                HostDisposed: owner._hooks.IsDisposed());
        }

        public bool IsProfilesFeatureAllowed()
            => owner._pluginAllows?.Invoke(PluginGate.ProfilesCapability) ?? true;

        public ProfileSummaryCandidateDto[] FindSummarizable(int threshold, int maxCandidates)
        {
            if (owner._unifiedMemory is not null && !owner.AttestedSummaryMode)
                return [];
            var raw = owner._profiles.FindSummarizable(threshold, maxCandidates, scopedKeysOnly: owner._unifiedMemory is not null);
            IEnumerable<Domain.Profiles.SummaryCandidate> filtered = raw;
            if (owner._unifiedMemory is not null)
            {
                filtered = raw.Where(c =>
                {
                    if (!LegacyProfileScopeKey.IsScopedKey(c.Uid)) return false;
                    if (!owner._profileProvenance!.TryGet(c.Uid, out var prov)) return false;
                    return !owner._unifiedMemory.IsLegacyProfileSuppressed(
                        prov.Subject, new UnifiedMemoryScope(prov.Driver, prov.Conversation));
                });
            }
            return filtered
                .Select(c => new ProfileSummaryCandidateDto(
                    c.Uid,
                    c.Name,
                    c.Scope,
                    c.ExistingSummary,
                    c.NewMessages as string[] ?? c.NewMessages.ToArray(),
                    c.ThroughSeq,
                    c.FoldedCount))
                .ToArray();
        }

        public Task<string?> SummarizePersonaAsync(string displayName, string existingSummary, string[] newMessages, int maxChars)
            => owner._brain.SummarizePersonaAsync(displayName, existingSummary, newMessages, maxChars);

        public async Task<string?> SummarizeCandidateAsync(ProfileSummaryCandidateDto candidate, int maxChars)
        {
            var displayName = owner._unifiedMemory is not null
                ? (string.IsNullOrWhiteSpace(candidate.Name) ? "成员" : candidate.Name)
                : (string.IsNullOrWhiteSpace(candidate.Name) ? $"QQ{candidate.Uid}" : candidate.Name);
            if (owner._unifiedMemory is null)
                return await owner._brain.SummarizePersonaAsync(displayName, candidate.ExistingSummary, candidate.NewMessages, maxChars).ConfigureAwait(false);

            if (!owner.AttestedSummaryMode || !LegacyProfileScopeKey.IsScopedKey(candidate.Uid)
                || !owner._profileProvenance!.TryGet(candidate.Uid, out var prov))
                return null;

            var ctx = new UnifiedMemoryContext(prov.Subject, prov.Driver, prov.Conversation, IsReliableIdentity: true);
            UnifiedMemoryReceipt? receipt;
            try { receipt = owner._unifiedMemory.AuthorizeLegacyProfile(ctx, prov.Subject); }
            catch { return null; }
            if (receipt is null || !owner._unifiedMemory.ValidateReceipt(receipt, ctx))
                return null;

            if (_pendingReceipts.Count >= 256 && !_pendingReceipts.ContainsKey(candidate.Uid))
            {
                foreach (var staleKey in _pendingReceipts.Keys.Take(64).ToArray())
                    _pendingReceipts.TryRemove(staleKey, out _);
            }

            _pendingReceipts[candidate.Uid] = (receipt, ctx, candidate.NewMessages ?? Array.Empty<string>());
            using var memScope = MemoryUseScope.Begin();
            MemoryUseScope.RestrictDestination(prov.Driver, prov.Conversation);
            MemoryUseScope.Register(MemorySensitivityTier.LegacyProfile,
                commit => owner.AttestedSummaryMode && owner._unifiedMemory.TryUseReceipt(receipt, ctx, commit));

            string? result;
            try
            {
                result = await MemoryUseScope.Use(() => owner._brain.SummarizePersonaAsync(
                    displayName, candidate.ExistingSummary, candidate.NewMessages ?? Array.Empty<string>(), maxChars)).ConfigureAwait(false);
            }
            catch (MemoryUseInvalidatedException)
            {
                _pendingReceipts.TryRemove(candidate.Uid, out _);
                return null;
            }
            catch
            {
                _pendingReceipts.TryRemove(candidate.Uid, out _);
                throw;
            }

            if (!owner.AttestedSummaryMode || !owner._unifiedMemory.ValidateReceipt(receipt, ctx))
            {
                _pendingReceipts.TryRemove(candidate.Uid, out _);
                return null;
            }
            return result;
        }

        public void ApplySummary(string uid, string scope, string summary, long throughSeq, int foldedCount)
            => _ = TryApplySummary(uid, scope, summary, throughSeq, foldedCount);

        public bool TryApplySummary(string uid, string scope, string summary, long throughSeq, int foldedCount)
        {
            if (owner._unifiedMemory is null)
            {
                owner._profiles.ApplySummary(uid, scope, summary, throughSeq, foldedCount);
                return true;
            }

            if (!_pendingReceipts.TryRemove(uid, out var pending) || !owner.AttestedSummaryMode
                || !LegacyProfileScopeKey.IsScopedKey(uid))
                return false;

            return owner._unifiedMemory.TryUseReceipt(pending.Receipt, pending.Context, () =>
            {
                owner._profiles.ApplySummary(uid, scope, summary, throughSeq, foldedCount);
                owner._profiles.SetEvidence(uid, scope, BuildEvidenceJson(pending.Snippets, throughSeq));
            });
        }

        private static string BuildEvidenceJson(string[] snippets, long throughSeq)
        {
            var items = snippets
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .TakeLast(3)
                .Select(s => s.Trim())
                .Select(s => s.Length <= 80 ? s : s[..80] + "…")
                .ToArray();
            return JsonSerializer.Serialize(new { throughSeq, evidence = items });
        }

        public void Log(string message) => owner._hooks.Log(message);

        public void LogInternal(string message) => FileLog.Write("Profiles", message);
    }
}

/// <summary>
/// 后台巡检要用到的宿主能力（由 BotAgentHost 提供实现）。
/// </summary>
/// <param name="Log">普通运行日志（写文件 + 推面板）。</param>
/// <param name="IsDisposed">宿主是否已经在收摊（收摊中不再启动新的一轮）。</param>
public readonly record struct BotSchedulerHooks(
    Action<string> Log,
    Func<bool> IsDisposed);
