using System.Collections.Concurrent;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Music;

/// <summary>
/// 听歌 / 分享歌的宿主薄门面：冷却、一次性听歌笔记、面板自测、听歌与分享回执编排全部归
/// <c>preset.feature.music</c> 独立包经 <see cref="IMusicRuntimeContribution" /> 拥有；缺包或禁用时 fail-closed。
/// </summary>
public sealed class MusicUseCase : IMusicUseCase
{
    public const string ProviderId = "preset.feature.music";

    private readonly MusicService? _music;
    private readonly IQqChatSource _source;
    private readonly Action<string> _log;
    private readonly PluginManager? _plugins;
    private readonly AsyncLocal<ReceiptCapture?> _receiptCapture = new();
    private IMusicRuntimeContribution? _direct;

    private sealed class ReceiptCapture { public SendResult? Last; }

    public MusicUseCase(
        MusicService? music,
        IQqChatSource source,
        Action<string> log,
        PluginManager? plugins = null,
        IMusicRuntimeContribution? direct = null)
    {
        _music = music;
        _source = source;
        _log = log;
        _plugins = plugins;
        _direct = direct;
    }

    /// <summary>仅供无插件宿主的独立单测注入包内真实运行时实现。</summary>
    public void BindDirect(IMusicRuntimeContribution runtime) => _direct = runtime;

    /// <summary>供宿主装配层注册到插件服务容器，向 <c>preset.feature.music</c> 提供窄宿主端口。</summary>
    public IMusicHostPorts CreateHostPorts() => new HostPorts(this);

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private IMusicRuntimeContribution? CurrentRuntime
        => _direct ?? (_plugins is not null && _plugins.TryGetContribution<IMusicRuntimeContribution>(ProviderId, out var runtime)
            ? runtime : null);

    private IMusicPolicyContribution? CurrentPolicy
        => _direct ?? (_plugins is not null && _plugins.TryGetContribution<IMusicPolicyContribution>(ProviderId, out var policy)
            ? policy : null);

    /// <summary>音乐插件是否可用（无插件宿主的独立单测放行；真实宿主按包就绪态 fail-closed）。</summary>
    public bool IsPluginAvailable => _direct is not null || _plugins is null || _plugins.IsReady(ProviderId);

    /// <summary>音乐服务就绪（宿主媒体依赖就绪且独立包已启用）。</summary>
    public bool IsReady => _music is not null && (_direct is not null || (_plugins is not null && _plugins.IsReady(ProviderId)));

    /// <summary>最近一次「面板自测听歌」测的是哪首（由独立包持有）。</summary>
    public string? LastTestHeader
    {
        get
        {
            try { return CurrentRuntime?.LastTestHeader; }
            catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
        }
    }

    public async Task<string?> TestByNameAsync(string song, CancellationToken ct)
    {
        if (CurrentRuntime is not { } runtime) return null;
        try { return await runtime.TestByNameAsync(song, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public Task<string?> DescribeShareAsync(MusicShare share, string sender)
        => DescribeShareAsync(share, sender, CancellationToken.None);

    public async Task<string?> DescribeShareAsync(MusicShare share, string sender, CancellationToken ct)
    {
        if (CurrentRuntime is not { } runtime) return null;
        try
        {
            return await runtime.DescribeShareAsync(
                share.Platform, share.SongId, share.Title, share.Artist, share.PageUrl, share.DirectAudioUrl, sender, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public bool TryBeginListen(string sourceKey, int cooldownSeconds, out string reason)
    {
        var policy = CurrentPolicy;
        if (policy is null)
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }

        try { return policy.TryBeginListen(sourceKey, cooldownSeconds, Clock.Now, out reason); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }
    }

    public async Task<string?> ListenAsync(string sourceKey, string song, string requester, CancellationToken ct = default)
    {
        if (CurrentRuntime is not { } runtime) return null;
        try { return await runtime.ListenAsync(sourceKey, song, requester, ct).ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public void AddNote(string sourceKey, string note)
    {
        try { CurrentPolicy?.AddNote(sourceKey, note); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public bool HasNote(string sourceKey)
    {
        try { return CurrentPolicy?.HasNote(sourceKey) == true; }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return false; }
    }

    public string? TakeNote(string sourceKey)
    {
        try { return CurrentPolicy?.TakeNote(sourceKey); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return null; }
    }

    public async Task<bool> ShareAsync(string song, bool isGroup, long targetId, CancellationToken ct = default)
        => (await ShareWithReceiptAsync(song, isGroup, targetId, ct)).Ok;

    public async Task<SendResult> ShareWithReceiptAsync(string song, bool isGroup, long targetId, CancellationToken ct = default)
    {
        var runtime = CurrentRuntime;
        if (runtime is null)
        {
            return new(false);
        }

        var previous = _receiptCapture.Value;
        var box = new ReceiptCapture();
        _receiptCapture.Value = box;
        try
        {
            var outcome = await runtime.ShareWithReceiptAsync(song, isGroup, targetId, ct).ConfigureAwait(false);
            if (box.Last is { } captured)
            {
                return captured with { Ok = outcome.Ok };
            }

            return new(outcome.Ok, outcome.MessageId);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            return new(false);
        }
        finally
        {
            _receiptCapture.Value = previous;
        }
    }

    private MusicSendOutcome CaptureSend(SendResult sent)
    {
        if (_receiptCapture.Value is { } box)
        {
            box.Last = sent;
        }

        return new(
            Ok: sent.Ok,
            HasDeliveryReceipt: sent.Delivery is not null,
            DeliverySucceeded: sent.Delivery?.IsSuccess == true,
            DeliveryStatus: sent.Delivery?.Status.ToString(),
            ErrorMessage: null,
            PlatformMessageId: null,
            MessageId: sent.MessageId,
            ReceiptToken: null);
    }

    private sealed class HostPorts(MusicUseCase owner) : IMusicHostPorts
    {
        public bool IsMediaReady => owner._music is not null;

        public bool IsSourceConnected => owner._source.IsConnected;

        public Task<string?> DescribeByNameAsync(string keyword, string senderName, CancellationToken ct)
            => owner._music is null
                ? Task.FromResult<string?>(null)
                : owner._music.DescribeByNameAsync(keyword, senderName, ct);

        public Task<string?> DescribeShareAsync(
            string platform,
            string? songId,
            string? title,
            string? artist,
            string? pageUrl,
            string? directAudioUrl,
            string senderName,
            CancellationToken ct)
            => owner._music is null
                ? Task.FromResult<string?>(null)
                : owner._music.DescribeAsync(
                    new MusicShare(platform, songId, title, artist, pageUrl, directAudioUrl),
                    senderName,
                    ct);

        public Task<string?> ResolveSongIdByNameAsync(string keyword, CancellationToken ct)
            => owner._music is null
                ? Task.FromResult<string?>(null)
                : owner._music.ResolveSongIdByNameAsync(keyword, ct);

        public async Task<MusicSendOutcome> SendMusicAsync(bool isGroup, long targetId, string songId, string title, CancellationToken ct)
        {
            var sent = await owner._source.SendMusicWithReceiptAsync(isGroup, targetId, "163", songId, title: title, ct: ct).ConfigureAwait(false);
            return owner.CaptureSend(sent);
        }

        public async Task<MusicSendOutcome> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct)
        {
            var sent = await owner._source.SendTextAsync(isGroup, targetId, text, ct).ConfigureAwait(false);
            return owner.CaptureSend(sent);
        }

        public void Log(string message) => owner._log(message);
    }
}
