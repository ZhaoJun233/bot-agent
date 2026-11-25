using BotAgent.Engine.Runtime;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Stickers;

/// <summary>
/// 表情包宿主薄门面：将收藏、描述队列、巡检定时器、自巡检、QQ 收藏导入与频率门全部委派给
/// <c>preset.feature.stickers</c> 独立包（<see cref="IStickerRuntimeContribution" /> /
/// <see cref="IStickerPolicyContribution" />）。缺包或禁用时 fail-closed，宿主不保留第二份实现或定时器。
/// </summary>
public sealed class StickerService : IDisposable
{
    public const string ProviderId = "preset.feature.stickers";

    private readonly IStickerRepository _store;
    private readonly IModelClient _brain;
    private readonly IQqChatSource _source;
    private readonly ISettingsBox _box;
    private readonly Action<string> _log;
    private readonly PluginManager? _plugins;
    private IStickerPolicyContribution? _directPolicy;
    private IStickerRuntimeContribution? _directRuntime;
    private IDisposable? _directDisposable;

    private AppSettings _settings => _box.Current;

    public Func<bool>? PluginEnabled { get; set; }

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private IStickerPolicyContribution? CurrentPolicy
        => _directPolicy ?? (_plugins is not null && _plugins.TryGetContribution<IStickerPolicyContribution>(ProviderId, out var policy)
            ? policy : null);

    private IStickerRuntimeContribution? CurrentRuntime
        => _directRuntime ?? (_plugins is not null && _plugins.TryGetContribution<IStickerRuntimeContribution>(ProviderId, out var runtime)
            ? runtime : null);

    public bool CanOperate
        => _settings.EnableStickers
           && _settings.StickerLibraryMax > 0
           && (PluginEnabled?.Invoke() ?? true)
           && (_directRuntime is not null || (_plugins is not null && _plugins.IsReady(ProviderId)));

    public StickerService(IStickerRepository store, IModelClient brain, IQqChatSource source, ISettingsBox box, Action<string> log, PluginManager? plugins = null)
    {
        _store = store;
        _brain = brain;
        _source = source;
        _box = box;
        _log = log;
        _plugins = plugins;
    }

    /// <summary>仅供无 PluginRuntime 的单测注入独立包实现；生产装配一律走 <see cref="PluginManager" />。</summary>
    public void BindDirect(IStickerPolicyContribution policy, IStickerRuntimeContribution runtime)
    {
        _directPolicy = policy;
        _directRuntime = runtime;
        _directDisposable = runtime as IDisposable;
    }

    /// <summary>为 <c>preset.feature.stickers</c> 构造最小受控宿主端口（不暴露 SettingsBox 或原始数据库）。</summary>
    public IStickerHostPorts CreateHostPorts() => new HostPorts(this);

    public IStickerRepository Store => _store;

    public int PendingDescribe
    {
        get
        {
            try { return CurrentRuntime?.PendingDescribe ?? 0; }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return 0; }
        }
    }

    public long DescribeDone
    {
        get
        {
            try { return CurrentRuntime?.DescribeDone ?? 0; }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return 0; }
        }
    }

    public void Load(string runtimeRoot) => _store.Load(runtimeRoot);

    public void EnsureTimer(bool enabled)
    {
        try { CurrentRuntime?.EnsureTimer(enabled); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public Task CollectAsync(List<string> urls, string fromUid, long fromGroup, long messageId = 0)
        => CollectAsync(urls, fromUid, fromGroup, messageId, null, CancellationToken.None);

    internal async Task CollectAsync(List<string> urls, string fromUid, long fromGroup, long messageId,
        Func<bool>? canStartExternalIo, CancellationToken ct = default)
    {
        if (CurrentRuntime is not { } runtime) return;
        try
        {
            await runtime.CollectAsync(urls?.ToArray() ?? Array.Empty<string>(), fromUid, fromGroup, messageId, canStartExternalIo, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public void QueueDescribe(string id)
    {
        try { CurrentRuntime?.QueueDescribe(id); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public async Task<string> CurateAsync(bool force, CancellationToken ct = default)
    {
        if (CurrentRuntime is not { } runtime)
            return "表情包功能已停用（插件未就绪），未执行巡检";
        try { return await runtime.CurateAsync(force, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        { return "表情包功能已停用（插件未就绪），未执行巡检"; }
    }

    public async Task<string> ImportFromAlbumAsync(int limit = 30, CancellationToken ct = default)
    {
        if (CurrentRuntime is not { } runtime)
            return "表情包功能已停用（插件未就绪），未执行导入";
        try { return await runtime.ImportFromAlbumAsync(limit, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        { return "表情包功能已停用（插件未就绪），未执行导入"; }
    }

    public bool Allow(string sourceKey, string stickerId, out string reason)
    {
        var policy = CurrentPolicy;
        if (policy is null)
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }

        try { return policy.Allow(sourceKey, stickerId, Math.Max(0, _settings.StickerCooldownSeconds), EngineClock.Now, out reason); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }
    }

    public async Task<bool> SendAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo, string sourceKey)
        => (await SendWithReceiptAsync(isGroup, targetId, sticker, replyTo, sourceKey)).Ok;

    public async Task<SendResult> SendWithReceiptAsync(bool isGroup, long targetId, StickerRecord sticker, long? replyTo, string sourceKey)
    {
        var invoked = false;
        var official = PlatformSourceExecution.Current?.Context.PlatformId == PlatformId.QqOfficial;
        try
        {
            var bytes = await _store.ReadBytesAsync(sticker);
            await ReplyCommitBoundary.BeforeSourceSendAsync(_source);
            invoked = true;
            var receipt = await _source.SendImageWithReceiptAsync(isGroup, targetId, bytes, replyToMessageId: replyTo);
            if (ReplyDeliveryResults.Confirmed(receipt))
            {
                var sinceText = string.Empty;
                try { sinceText = CurrentPolicy?.NoteSent(sourceKey, sticker.Id, EngineClock.Now) ?? string.Empty; }
                catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
                _log($"已发表情包 #{sticker.Id}{sinceText}");
            }

            return receipt with { Ok = ReplyDeliveryResults.Confirmed(receipt) };
        }
        catch (Exception ex) when (!ReplyCommitBoundary.IsActive || ex is not OperationCanceledException)
        {
            _log("[Sticker] delivery_failed reason=image_send_failed");
            return new(false) { Delivery = !official ? null : invoked
                ? DeliveryResult.Unknown("image_outcome_unknown") : DeliveryResult.Rejected("image_preparation_failed") };
        }
    }

    public void Dispose()
    {
        try { CurrentRuntime?.EnsureTimer(false); } catch { }
        _directDisposable?.Dispose();
        _directDisposable = null;
    }

    private sealed class HostPorts(StickerService owner) : IStickerHostPorts
    {
        public int StoreCount => owner._store.Count;

        public StickerItemSnapshot[] SnapshotStore()
            => owner._store.Snapshot().Select(ToSnapshot).ToArray();

        public StickerItemSnapshot? FindInStore(string id)
            => owner._store.Find(id) is { } r ? ToSnapshot(r) : null;

        public StickerItemSnapshot? AddToStore(byte[] data, string ext, string? fromUid, long fromGroup)
            => owner._store.Add(data, ext, fromUid, fromGroup) is { } r ? ToSnapshot(r) : null;

        public string[] EnforceStoreLimit(int maxCount)
            => owner._store.EnforceLimit(maxCount).Select(r => r.Id).ToArray();

        public Task<byte[]> ReadStickerBytesAsync(string id)
        {
            var item = owner._store.Find(id) ?? throw new FileNotFoundException("sticker_not_found", id);
            return owner._store.ReadBytesAsync(item);
        }

        public void MarkDescribeFailed(string id) => owner._store.MarkDescribeFailed(id);

        public void SetDescription(string id, string? description, string[]? tags, bool? isSticker)
            => owner._store.SetDescription(id, description, tags, isSticker);

        public bool RemoveFromStore(string id, string reason) => owner._store.Remove(id, reason);

        public async Task<StickerDownloadedImage?> DownloadImageAsync(string url, long? messageId, CancellationToken ct)
            => await owner._brain.DownloadImageAsync(url, ct, messageId).ConfigureAwait(false) is { } img
                ? new StickerDownloadedImage(img.Data, img.Ext) : null;

        public async Task<StickerDescribeResult> DescribeStickerAsync(byte[] bytes, string mime, CancellationToken ct)
        {
            var (desc, tags, isSticker) = await owner._brain.DescribeStickerAsync(bytes, mime).ConfigureAwait(false);
            return new StickerDescribeResult(desc, tags?.ToArray(), isSticker);
        }

        public async Task<StickerCurateDecision> CurateStickersAsync(string table, int maxDelete, CancellationToken ct)
        {
            var (wanted, reason) = await owner._brain.CurateStickersAsync(table, maxDelete, ct).ConfigureAwait(false);
            return new StickerCurateDecision(wanted.ToArray(), reason);
        }

        public async Task<string[]> FetchCustomFacesAsync(int limit, CancellationToken ct)
            => (await owner._source.FetchCustomFacesAsync(limit, ct).ConfigureAwait(false)).ToArray();

        public StickerRuntimeOptions GetOptions()
        {
            var s = owner._settings;
            return new StickerRuntimeOptions(s.EnableStickers, s.StickerLibraryMax, s.StickerCurateIntervalSeconds, s.StickerCooldownSeconds);
        }

        public bool IsPluginGateAllowed() => owner.PluginEnabled?.Invoke() ?? true;

        public DateTimeOffset UtcNow => EngineClock.UtcNow;

        public DateTimeOffset Now => EngineClock.Now;

        public void Log(string message) => owner._log(message);

        private static StickerItemSnapshot ToSnapshot(StickerRecord r)
            => new(r.Id, r.Ext, r.Desc ?? string.Empty,
                r.Tags is { Count: > 0 } ? r.Tags.ToArray() : Array.Empty<string>(),
                r.Described, r.IsSticker, r.DescribeAttempts, r.Uses, r.AddedAt, r.LastUsedAt);
    }
}
