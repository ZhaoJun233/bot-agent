using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Engine.Runtime;
using BotAgent.Services.Links;
using BotAgent.Services.Music;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Plugins;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;

namespace BotAgent.Services.Reply;

/// <summary>
/// 媒体类入站（#106 拆分第 4 步）：群友发的图收进表情包库、音乐分享去查歌词与波形、
/// 链接去取标题摘要、首个群消息补一次历史上下文。
///
/// 为什么单独成类：这些活儿**全是 fire-and-forget**（网关在接收循环里同步调我们，这里绝不能等），
/// 失败的语义也和回复不同 —— 它不影响"这一轮回不回"，只决定"下一轮模型手里有没有料"。
/// 把它们和回复编排放在一起时，任何一次改动都要重新确认"会不会把接收线程堵住"。
///
/// 两类产出交给下一轮：
/// - <see cref="PendingLinkTask" /> / <see cref="TakeLinkNote" />：本轮链接预览的快慢由上下文构建决定；
/// - 音乐分析结果直接写进 music 用例的 note（由上下文构建取走）。
///
/// 每个后台作业真正开始前都要重新复核插件 / 平台 / 来源实例（排队期间可能已被关掉、来源可能已退休）。
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
    private readonly Action<BotConversation> _requestReply;
    private readonly Action<BotConversation, long> _ensureGroupContext;

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
        Action<BotConversation> requestReply,
        Action<BotConversation, long> ensureGroupContext)
    {
        _box = box;
        _hooks = hooks;
        _platformPolicies = platformPolicies;
        _allowsFeature = allowsFeature;
        _stickers = stickers;
        _music = music;
        _links = links;
        _queueBackground = queueBackground;
        _requestReply = requestReply;
        _ensureGroupContext = ensureGroupContext;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>这一会话正在跑的链接预览；没有就是 null（上下文构建据此决定要不要等 2.5 秒）。</summary>
    public Task? PendingLinkTask(string sourceKey) =>
        _linkTasks.TryGetValue(sourceKey, out var task) ? task : null;

    /// <summary>取走这一会话最近一次的链接描述（取走就清，下一轮不会重复塞给模型）。</summary>
    public string? TakeLinkNote(string sourceKey) =>
        _linkNotes.TryRemove(sourceKey, out var note) ? note : null;

    /// <summary>
    /// 媒体类入站：群友发的图自动收进表情包库（后台下载）、音乐分享去查歌词与波形、
    /// 链接去取标题摘要、首个群消息补一次历史上下文。返回识别到的音乐分享（真的有歌时这一轮**先不回复**）。
    /// 一律 fire-and-forget：网关是在接收循环里同步调我们的，这里绝不能等。
    /// </summary>
    public IReadOnlyList<MusicShare>? Register(BotConversation conversation, QqChatMessage msg)
    {
        var policy = PlatformSourceExecution.Current is { } source
            ? _platformPolicies.Resolve(source.Context) : _platformPolicies.ResolveForChannel(msg.Channel);
        var stickersEnabled = policy.Feature("stickers", _settings.EnableStickers).Enabled && _allowsFeature(PluginGate.StickersCapability);
        var musicEnabled = policy.Feature("music", _settings.EnableMusic).Enabled && _allowsFeature(PluginGate.MusicCapability);
        var linksEnabled = policy.Feature("linkpreview", _settings.EnableLinkPreview).Enabled && _allowsFeature(PluginGate.WebSearchCapability);

        // 表情包：群友发的图自动收进库（后台下载，不阻塞接收线程）
        if (stickersEnabled && _settings.StickerLibraryMax > 0 && msg.IsGroup && msg.ImageUrls is { Count: > 0 })
        {
            var urls = msg.ImageUrls.ToList();
            var uid = msg.UserId.ToString();
            var group = msg.GroupId;
            var mid = msg.MessageId;
            _queueBackground("stickers:" + mid, "stickers:" + mid, async _ =>
            {
                if (!QueuedInboundCapabilityAllowed(PluginGate.StickersCapability, "stickers",
                        _settings.EnableStickers, policy, "表情包收集"))
                {
                    return;
                }

                await _stickers.CollectAsync(urls, uid, group, mid,
                    () => QueuedInboundCapabilityAllowed(PluginGate.StickersCapability, "stickers",
                        _settings.EnableStickers, policy, "表情包下载"));
            });
        }

        // 听音乐：识别到分享就后台去查歌词 + 下一份低码率音频分析波形。
        // 有歌的时候**先不回复** —— 等分析结果回来再让模型开口，否则它只能对着一个歌名瞎聊。
        var musicShares = musicEnabled ? msg.MusicShares : null;
        if (musicShares is { Count: > 0 })
        {
            _queueBackground("music-inbound:" + conversation.SourceKey, "music-inbound:" + conversation.SourceKey, _ =>
                HandleMusicAsync(conversation, msg.SenderName, musicShares.ToList(), policy));
        }

        // 链接：群里发的 URL（包括分享卡片里那个）真去打开看一眼，取回标题/摘要。
        // 有音乐分享时跳过 —— 音乐那条路自己会处理链接，不必看两遍。
        var linkUrls = musicShares is { Count: > 0 } || _links is null || !linksEnabled
            ? []
            : LinkExtractor.Extract(msg.Text, Math.Clamp(_settings.LinkPreviewMax, 0, 5));
        if (linkUrls.Count > 0)
        {
            var key = conversation.SourceKey;
            var urls = linkUrls.ToList();
            var previewId = "link-preview:" + key + ":" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(string.Join("\n", urls))));
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // 先登记等待句柄，再入队：队列可能在派发后立刻执行并清掉这个键。
            _linkTasks[key] = pending.Task;
            var accepted = _queueBackground(previewId, previewId, async ct =>
            {
                try
                {
                    if (!QueuedInboundCapabilityAllowed(PluginGate.WebSearchCapability, "linkpreview",
                            _settings.EnableLinkPreview, policy, "链接预览"))
                    {
                        return;
                    }

                    var note = await _links!.DescribeAsync(urls, ct);
                    if (ct.IsCancellationRequested) return;

                    // 预览期间插件可能被关：已经发出的请求无法撤回，但结果不能再留到下一轮。
                    if (!QueuedInboundCapabilityAllowed(PluginGate.WebSearchCapability, "linkpreview",
                            _settings.EnableLinkPreview, policy, "链接预览"))
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(note))
                    {
                        _linkNotes[key] = note!;
                        _hooks.Log($"[Link] 已看过 {urls.Count} 个链接：{string.Join("、", urls.Select(u => u.Length > 48 ? u[..48] + "…" : u))}");
                    }
                }
                catch (OperationCanceledException)
                {
                    // 停机/超时取消：不再把链接预览当成失败刷日志。
                }
                catch (Exception ex)
                {
                    _hooks.Log($"[Link] 预览失败: {ex.Message}");
                }
                finally
                {
                    _linkTasks.TryRemove(key, out _);
                    pending.TrySetResult();
                }
            });
            if (!accepted)
            {
                pending.TrySetResult();
            }
        }

        // 首个群消息时补历史上下文（没有会话列表可点，只能在这里补）
        if (msg.IsGroup)
        {
            _ensureGroupContext(conversation, msg.GroupId);
        }

        return musicShares;
    }

    /// <summary>
    /// 听音乐：拿歌词 + 低码率音频做波形分析，把实测到的事实留给下一轮回复。
    /// 分析完成后单独触发一次发言机会 —— 这样模型是“听完再说”，而不是先瞎猜一遍再补课。
    /// </summary>
    private async Task HandleMusicAsync(BotConversation conversation, string sender, List<MusicShare> shares,
        EffectivePlatformPolicy turnPolicy)
    {
        if (!_music.IsReady)
        {
            return;
        }

        var heard = false;
        foreach (var share in shares)
        {
            // 多首分享是多次外部调用：每次真正开始前都复核，不能只在作业入口看一次。
            if (!QueuedInboundCapabilityAllowed(PluginGate.MusicCapability, "music",
                    _settings.EnableMusic, turnPolicy, "听歌"))
            {
                return;
            }

            try
            {
                var note = await _music.DescribeShareAsync(share, sender);
                if (string.IsNullOrWhiteSpace(note))
                {
                    continue;
                }

                // 一次发好几首时合并，别让后一首盖掉前一首
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
                _settings.EnableMusic, turnPolicy, "听歌后回复"))
        {
            _requestReply(conversation); // 没有触发消息 → 不引用（沿用 replyTo 那套规则）
        }
    }

    /// <summary>
    /// 后台作业真正开始前的通用实时复核：入队时检查只决定“是否值得排队”，
    /// 排队期间插件、平台功能或来源租约都可能被关闭；这些作业不能在关闭后继续发起外部 I/O。
    /// </summary>
    private bool QueuedInboundCapabilityAllowed(
        string capability,
        string feature,
        bool fallbackEnabled,
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
        var enabled = policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled
            && policy.Feature(feature, fallbackEnabled).Enabled;
        if (!enabled)
        {
            _hooks.Log($"[媒体] 后台{phase}已取消（平台或功能已关闭）");
        }

        return enabled;
    }
}
