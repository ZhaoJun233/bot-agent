using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Engine.Runtime;
using BotAgent.Services.Documents;
using BotAgent.Services.Links;
using BotAgent.Services.Music;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;

namespace BotAgent.Services.Reply;

/// <summary>
/// 媒体类入站（#106 拆分第 4 步 + Phase 2 云端 STT / 受控文档入站）：
/// 群友发的图收进表情包库、音乐分享去查歌词与波形、语音走云端 STT 转写、
/// 文件附件走受控文档提取、链接去取标题摘要、首个群消息补一次历史上下文。
///
/// 一律 fire-and-forget：网关在接收循环里同步调我们，这里绝不能等。
/// 每个后台作业真正开始前都要重新复核插件 / 平台 / 会话档案 / 来源实例。
/// </summary>
internal sealed class ReplyInboundMedia
{
    private readonly ISettingsBox _box;
    private readonly ReplyHooks _hooks;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly Func<string, bool> _allowsFeature;
    private readonly StickerService _stickers;
    private readonly IMusicUseCase _music;
    private readonly ILinkPreviewer? _links;
    private readonly Func<string, string, Func<CancellationToken, Task>, bool> _queueBackground;
    private readonly Func<BotConversation, ChatMessage, Action> _captureReply;
    private readonly Action<BotConversation, long> _ensureGroupContext;
    private readonly IQqChatSource? _source;
    private readonly IVoiceUseCase? _voice;
    private readonly IInboundDocumentProcessor? _documents;
    private readonly Action<string, ChatMessage>? _notifyMessageUpdated;
    private readonly Action? _saveRegistry;
    private readonly object _rewriteGate = new();

    /// <summary>每个会话正在跑的链接预览（回复前短暂等一下：快站点能当轮就用上）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _linkTasks = new();
    /// <summary>每个会话最近一次“链接里写了啥”的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _linkNotes = new();

    public ReplyInboundMedia(
        ISettingsBox box,
        ReplyHooks hooks,
        IPlatformPolicyResolver platformPolicies,
        Func<string, bool> allowsFeature,
        StickerService stickers,
        IMusicUseCase music,
        ILinkPreviewer? links,
        Func<string, string, Func<CancellationToken, Task>, bool> queueBackground,
        Func<BotConversation, ChatMessage, Action> captureReply,
        Action<BotConversation, long> ensureGroupContext,
        IQqChatSource? source = null,
        IVoiceUseCase? voice = null,
        IInboundDocumentProcessor? documents = null,
        Action<string, ChatMessage>? notifyMessageUpdated = null,
        Action? saveRegistry = null)
    {
        _box = box;
        _hooks = hooks;
        _platformPolicies = platformPolicies;
        _allowsFeature = allowsFeature;
        _stickers = stickers;
        _music = music;
        _links = links;
        _queueBackground = queueBackground;
        _captureReply = captureReply;
        _ensureGroupContext = ensureGroupContext;
        _source = source;
        _voice = voice;
        _documents = documents;
        _notifyMessageUpdated = notifyMessageUpdated;
        _saveRegistry = saveRegistry;
    }

    private AppSettings _settings => _box.Current;

    public Task? PendingLinkTask(string sourceKey) =>
        _linkTasks.TryGetValue(sourceKey, out var task) ? task : null;

    public string? TakeLinkNote(string sourceKey) =>
        _linkNotes.TryRemove(sourceKey, out var note) ? note : null;

    public IReadOnlyList<MusicShare>? Register(BotConversation conversation, QqChatMessage msg, ChatMessage appended)
        => RegisterWithHold(conversation, msg, appended, out _);

    public IReadOnlyList<MusicShare>? RegisterWithHold(
        BotConversation conversation,
        QqChatMessage msg,
        ChatMessage appended,
        out bool holdImmediateReply)
    {
        holdImmediateReply = false;
        var policy = PlatformSourceExecution.Current is { } source
            ? _platformPolicies.Resolve(source.Context) : _platformPolicies.ResolveForChannel(msg.Channel);
        var conversationKey = conversation.SourceKey;
        var conv = ConversationProfileResolver.Resolve(_settings, policy, conversationKey);
        var stickersEnabled = conv.FeatureEnabled("stickers") && _allowsFeature(PluginGate.StickersCapability);
        var musicEnabled = conv.FeatureEnabled("music") && _allowsFeature(PluginGate.MusicCapability);
        var linksEnabled = conv.FeatureEnabled("linkpreview") && _allowsFeature(PluginGate.WebSearchCapability);
        var docsEnabled = conv.FeatureEnabled("documents") && _allowsFeature(PluginGate.DocumentsCapability);

        if (stickersEnabled && _settings.StickerLibraryMax > 0 && msg.IsGroup && msg.ImageUrls is { Count: > 0 })
        {
            var urls = msg.ImageUrls.ToList();
            var uid = msg.UserId.ToString();
            var group = msg.GroupId;
            var mid = msg.MessageId;
            _queueBackground("stickers:" + mid, "stickers:" + mid, async ct =>
            {
                if (!QueuedInboundCapabilityAllowed(PluginGate.StickersCapability, "stickers",
                        conversationKey, policy, "表情包收集"))
                    return;

                await _stickers.CollectAsync(urls, uid, group, mid,
                    () => QueuedInboundCapabilityAllowed(PluginGate.StickersCapability, "stickers",
                        conversationKey, policy, "表情包下载"), ct);
            });
        }

        var musicShares = musicEnabled ? msg.MusicShares : null;
        if (musicShares is { Count: > 0 })
        {
            var requestReply = _captureReply(conversation, appended);
            conversation.HasPendingReply = false;
            holdImmediateReply = true;
            _queueBackground("music-inbound:" + conversationKey, "music-inbound:" + conversationKey, ct =>
                HandleMusicAsync(conversation, msg.SenderName, musicShares.ToList(), policy, requestReply, ct));
        }

        if (TryQueueVoiceStt(conversation, msg, appended, policy))
            holdImmediateReply = true;

        if (docsEnabled && TryQueueDocumentInbound(conversation, msg, appended, policy))
            holdImmediateReply = true;

        QueueLinkPreviewIfNeeded(conversationKey, msg.Text, musicShares, linksEnabled, policy);

        if (msg.IsGroup)
            _ensureGroupContext(conversation, msg.GroupId);

        return musicShares;
    }

    private void QueueLinkPreviewIfNeeded(
        string key,
        string text,
        IReadOnlyList<MusicShare>? musicShares,
        bool linksEnabled,
        EffectivePlatformPolicy policy)
    {
        var linkUrls = musicShares is { Count: > 0 } || _links is null || !linksEnabled
            ? []
            : LinkExtractor.Extract(text, Math.Clamp(_settings.LinkPreviewMax, 0, 5));
        if (linkUrls.Count == 0)
            return;

        var urls = linkUrls.ToList();
        var previewId = "link-preview:" + key + ":" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("\n", urls))));
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _linkTasks[key] = pending.Task;
        var accepted = _queueBackground(previewId, previewId, async ct =>
        {
            try
            {
                if (!QueuedInboundCapabilityAllowed(PluginGate.WebSearchCapability, "linkpreview",
                        key, policy, "链接预览"))
                    return;

                var note = await _links!.DescribeAsync(urls, ct);
                if (ct.IsCancellationRequested) return;
                if (!QueuedInboundCapabilityAllowed(PluginGate.WebSearchCapability, "linkpreview",
                        key, policy, "链接预览"))
                    return;

                if (!string.IsNullOrWhiteSpace(note))
                {
                    _linkNotes[key] = note!;
                    _hooks.Log($"[Link] 已看过 {urls.Count} 个链接：{string.Join("、", urls.Select(u => u.Length > 48 ? u[..48] + "…" : u))}");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _hooks.Log($"[Link] 预览失败: {ex.Message}"); }
            finally
            {
                _linkTasks.TryRemove(key, out _);
                pending.TrySetResult();
            }
        });
        if (!accepted)
            pending.TrySetResult();
    }

    private bool TryQueueVoiceStt(
        BotConversation conversation,
        QqChatMessage msg,
        ChatMessage appended,
        EffectivePlatformPolicy policy)
    {
        if (_voice is not { IsSttEnabled: true } || _source is null || msg.VoiceAttachments is not { Count: > 0 })
            return false;

        var maxDuration = _voice.MaxSttDurationSeconds;
        var pendingVoices = new List<InboundVoiceAttachment>();
        foreach (var v in msg.VoiceAttachments.Take(4))
        {
            if (v.DurationSeconds is { } sec && sec > maxDuration)
            {
                _hooks.Log($"[Voice] STT skipped reason=audio_duration_exceeded seconds={sec} limit={maxDuration}");
                continue;
            }
            if (string.IsNullOrWhiteSpace(v.Transcript)
                && (!string.IsNullOrWhiteSpace(v.WavUrl) || !string.IsNullOrWhiteSpace(v.PlatformAudioRef)))
                pendingVoices.Add(v);
        }
        if (pendingVoices.Count == 0 || !InboundSttAllowed(conversation.SourceKey, policy))
            return false;

        var requestReply = _captureReply(conversation, appended);
        var jobId = $"voice-stt:{conversation.SourceKey}:{msg.MessageId}";
        if (!_queueBackground(jobId, jobId, ct =>
                HandleVoiceSttAsync(conversation, msg.MessageId, appended, pendingVoices, policy, requestReply, ct)))
        {
            return false;
        }

        conversation.HasPendingReply = false;
        return true;
    }

    private async Task HandleVoiceSttAsync(
        BotConversation conversation,
        long messageId,
        ChatMessage appended,
        List<InboundVoiceAttachment> voices,
        EffectivePlatformPolicy turnPolicy,
        Action requestReply,
        CancellationToken ct)
    {
        if (!InboundSttAllowed(conversation.SourceKey, turnPolicy) || _voice is not { IsSttEnabled: true } || _source is null)
        {
            if (appended.DirectToBot && !string.IsNullOrWhiteSpace(appended.Text) && appended.Text != "[语音]")
                requestReply();
            return;
        }

        var maxDuration = _voice.MaxSttDurationSeconds;
        var transcripts = new List<string>();
        foreach (var voice in voices)
        {
            if (ct.IsCancellationRequested || !InboundSttAllowed(conversation.SourceKey, turnPolicy))
            {
                if (!ct.IsCancellationRequested && appended.DirectToBot && !string.IsNullOrWhiteSpace(appended.Text) && appended.Text != "[语音]")
                    requestReply();
                return;
            }
            if (voice.DurationSeconds is { } sec && sec > maxDuration)
            {
                _hooks.Log($"[Voice] STT skipped reason=audio_duration_exceeded seconds={sec} limit={maxDuration}");
                continue;
            }
            try
            {
                var (bytes, remoteUrl, fileName) = await _source.ResolveInboundVoiceAsync(voice, messageId, ct).ConfigureAwait(false);
                (string? text, string _) = bytes is { Length: > 0 }
                    ? await _voice.TranscribeAsync(bytes, fileName, ct).ConfigureAwait(false)
                    : !string.IsNullOrWhiteSpace(remoteUrl)
                        ? await _voice.TranscribeFromUrlAsync(remoteUrl!, fileName, ct).ConfigureAwait(false)
                        : (null, "voice_payload_empty");
                if (!string.IsNullOrWhiteSpace(text))
                    transcripts.Add(text!);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _hooks.Log($"[Voice] 入站语音解析失败 type={ex.GetType().Name}");
            }
        }

        if (transcripts.Count > 0)
        {
            var joined = string.Join(" / ", transcripts);
            var rewritten = false;
            lock (_rewriteGate)
            {
                var current = appended.Text ?? string.Empty;
                var updated = current.Contains("平台参考转写: 暂无转写", StringComparison.Ordinal)
                    ? current.Replace("平台参考转写: 暂无转写", "云端语音转写: " + joined, StringComparison.Ordinal)
                    : current == "[语音]"
                        ? $"[语音消息（外部不可信转写）] {joined}"
                        : $"{current}\n[语音转写（外部不可信内容）]: {joined}";
                rewritten = conversation.RewriteText(appended, updated);
            }

            if (rewritten)
            {
                _notifyMessageUpdated?.Invoke(conversation.SourceKey, appended);
                _saveRegistry?.Invoke();
            }
        }

        if ((transcripts.Count > 0 || (appended.DirectToBot && !string.IsNullOrWhiteSpace(appended.Text) && appended.Text != "[语音]"))
            && PlatformSourceExecution.Current is not { IsCurrent: false })
            requestReply();
    }

    private bool TryQueueDocumentInbound(
        BotConversation conversation,
        QqChatMessage msg,
        ChatMessage appended,
        EffectivePlatformPolicy policy)
    {
        if (_documents is null || msg.FileAttachments is not { Count: > 0 })
            return false;

        var files = msg.FileAttachments.Take(2).ToList();
        var shouldTriggerReply = appended.DirectToBot;
        var requestReply = shouldTriggerReply ? _captureReply(conversation, appended) : null;
        var targetId = msg.IsGroup ? msg.GroupId : msg.UserId;
        var jobId = $"doc-inbound:{conversation.SourceKey}:{msg.MessageId}";
        if (!_queueBackground(jobId, jobId, ct =>
                HandleDocumentsAsync(conversation, msg.IsGroup, targetId, msg.MessageId, appended, files, policy, requestReply, ct)))
        {
            return false;
        }

        if (shouldTriggerReply)
            conversation.HasPendingReply = false;
        return true;
    }

    private async Task HandleDocumentsAsync(
        BotConversation conversation,
        bool isGroup,
        long targetId,
        long messageId,
        ChatMessage appended,
        List<InboundFileAttachment> files,
        EffectivePlatformPolicy turnPolicy,
        Action? requestReply,
        CancellationToken ct)
    {
        if (_documents is null || !QueuedInboundCapabilityAllowed(
                PluginGate.DocumentsCapability, "documents", conversation.SourceKey, turnPolicy, "文档入站解析"))
        {
            if (requestReply is not null && !string.IsNullOrWhiteSpace(appended.Text)
                && !appended.Text.StartsWith("[文件:", StringComparison.Ordinal))
                requestReply();
            return;
        }

        var previews = new List<string>();
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) return;
            if (!QueuedInboundCapabilityAllowed(
                    PluginGate.DocumentsCapability, "documents", conversation.SourceKey, turnPolicy, "文档提取"))
            {
                if (requestReply is not null && !string.IsNullOrWhiteSpace(appended.Text)
                    && !appended.Text.StartsWith("[文件:", StringComparison.Ordinal))
                    requestReply();
                return;
            }

            try
            {
                var (bytes, remoteUrl, resolvedName) = _source is not null
                    ? await _source.ResolveInboundFileAsync(file, isGroup, targetId, messageId, ct).ConfigureAwait(false)
                    : (null, file.DownloadUrl, file.FileName);
                var normalized = string.IsNullOrWhiteSpace(resolvedName) ? file : file with { FileName = resolvedName };
                var preview = await _documents.ProcessInboundAsync(
                    conversation.SourceKey, normalized, bytes, remoteUrl ?? file.DownloadUrl, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(preview))
                    previews.Add(preview!);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _hooks.Log($"[Documents] 入站附件处理失败 type={ex.GetType().Name}");
            }
        }

        if (previews.Count > 0)
        {
            var rewritten = false;
            lock (_rewriteGate)
            {
                var current = appended.Text ?? string.Empty;
                var updated = current.Length == 0
                    ? string.Join("\n\n", previews)
                    : current + "\n" + string.Join("\n\n", previews);
                rewritten = conversation.RewriteText(appended, updated);
            }

            if (rewritten)
            {
                _notifyMessageUpdated?.Invoke(conversation.SourceKey, appended);
                _saveRegistry?.Invoke();
            }
        }

        if (requestReply is not null)
        {
            if (QueuedInboundCapabilityAllowed(
                    PluginGate.DocumentsCapability, "documents", conversation.SourceKey, turnPolicy, "文档解析后回复")
                || (!string.IsNullOrWhiteSpace(appended.Text) && !appended.Text.StartsWith("[文件:", StringComparison.Ordinal)))
            {
                requestReply();
            }
        }
    }

    private async Task HandleMusicAsync(BotConversation conversation, string sender, List<MusicShare> shares,
        EffectivePlatformPolicy turnPolicy, Action requestReply, CancellationToken ct)
    {
        if (!_music.IsReady)
            return;

        var heard = false;
        foreach (var share in shares)
        {
            if (ct.IsCancellationRequested) return;
            if (!QueuedInboundCapabilityAllowed(PluginGate.MusicCapability, "music",
                    conversation.SourceKey, turnPolicy, "听歌"))
                return;

            try
            {
                var note = await _music.DescribeShareAsync(share, sender, ct);
                if (string.IsNullOrWhiteSpace(note))
                    continue;

                _music.AddNote(conversation.SourceKey, note!);
                heard = true;
                _hooks.Log($"[Music] 已听过：{share.Describe()}");
            }
            catch (Exception ex)
            {
                _hooks.Log($"[Music] 处理失败（{share.Describe()}）: {ex.Message}");
            }
        }

        if (heard && QueuedInboundCapabilityAllowed(PluginGate.MusicCapability, "music",
                conversation.SourceKey, turnPolicy, "听歌后回复"))
        {
            requestReply();
        }
    }

    private bool InboundSttAllowed(string conversationKey, EffectivePlatformPolicy turnPolicy)
    {
        if (!_allowsFeature(PluginGate.VoiceCapability) || _voice is not { IsSttEnabled: true })
            return false;
        if (PlatformSourceExecution.Current is { IsCurrent: false })
            return false;

        var policy = _platformPolicies.Resolve(turnPolicy.Context);
        if (!policy.Registered || !policy.Connected || !policy.Enabled || !policy.ChatEnabled || !policy.Capabilities.SupportsText)
            return false;
        if (policy.FeatureOverrides is not null && policy.FeatureOverrides.TryGetValue("voice", out var pAllowed) && !pAllowed)
            return false;

        var profile = ConversationProfileResolver.FindProfile(_settings, policy.Context, conversationKey);
        if (profile?.Capabilities?.Features is { IsDefaultOrEmpty: false } features
            && features.Any(f => f is { Feature: PlatformFeatureId.Voice, Enabled: false }))
            return false;

        return true;
    }

    private bool QueuedInboundCapabilityAllowed(
        string capability,
        string feature,
        string conversationKey,
        EffectivePlatformPolicy turnPolicy,
        string phase)
    {
        if (!_allowsFeature(capability))
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（插件已关闭）");
            return false;
        }

        if (PlatformSourceExecution.Current is { IsCurrent: false })
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（来源已退休）");
            return false;
        }

        var policy = _platformPolicies.Resolve(turnPolicy.Context);
        var conv = ConversationProfileResolver.Resolve(_settings, policy, conversationKey);
        var enabled = policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled
            && conv.FeatureEnabled(feature);
        if (!enabled)
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（平台或功能已关闭）");
        }

        return enabled;
    }
}
