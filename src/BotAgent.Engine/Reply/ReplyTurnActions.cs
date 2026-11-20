using BotAgent.Services.Plugins;
using BotAgent.Services.Evaluation;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Links;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.OneBot;
using BotAgent.Services.Panel;
using BotAgent.Services.Ports;
using BotAgent.Services.Ops;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Poke;
using BotAgent.Services.Platforms;
using BotAgent.Services.Qq;
using BotAgent.Services.Resilience;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Tools;
using BotAgent.Services.Tools;

namespace BotAgent.Services.Reply;

/// <summary>
/// 一轮回复的**动作执行**（#106 第 7 步）：模型在决策里额外要求"做点事"时，先把能立刻做的做掉，
/// 再把必须后台做的排进有界队列。
/// <para>
/// 分两类，都是为了不让回复变慢：
/// ① **当场结算**：表情包挑选（型号/审核/频率门）、戳一戳目标校验 —— 结果作为参数交给投递层；
/// ② **后台登记**：联网查资料、读页面、听歌、分享歌 —— 立刻回一句"稍等"，结果留给下一轮（或在允许时叫醒模型）。
/// </para>
/// <para>
/// 边界：这里**不发送任何回复**（投递层的事），**不改模型输出**（决策内容在解析时就定下了）。
/// 它只负责"动作要不要做、能不能做、排到哪个队列"，并把复核结果如实写进日志与轨迹。
/// 后台作业开始前必须重新复核（插件/功能/平台/会话都仍有效）：排队期间被关掉的作业不能再出网、不能再发。
/// </para>
/// </summary>
internal sealed class ReplyTurnActions(
    ISettingsBox box,
    ReplyHooks hooks,
    ReplyToolAvailability replyTools,
    IResearchUseCase research,
    IMusicUseCase music,
    StickerService stickers,
    VibeTracker vibes,
    ApprovalUseCase approvals,
    ParticipationUseCase participation,
    IPlatformPolicyResolver platformPolicies,
    ReplyDelivery delivery,
    IProtocolRiskBackoff? riskBackoff,
    Func<string, bool> allowsFeature,
    Func<string, string, Func<CancellationToken, Task>, bool> queueBackground,
    Action<BotConversation, AppSettings> requestReply)
{
    private readonly ISettingsBox _box = box;
    private readonly ReplyHooks _hooks = hooks;
    private readonly ReplyToolAvailability _replyTools = replyTools;
    private readonly IResearchUseCase _research = research;
    private readonly IMusicUseCase _music = music;
    private readonly StickerService _stickers = stickers;
    private readonly VibeTracker _vibes = vibes;
    private readonly ApprovalUseCase _approvals = approvals;
    private readonly ParticipationUseCase _participation = participation;
    private readonly IPlatformPolicyResolver _platformPolicies = platformPolicies;
    private readonly ReplyDelivery _delivery = delivery;
    private readonly IProtocolRiskBackoff? _riskBackoff = riskBackoff;
    private readonly Func<string, bool> _allowsFeature = allowsFeature;
    private readonly Func<string, string, Func<CancellationToken, Task>, bool> _queueBackground = queueBackground;
    private readonly Action<BotConversation, AppSettings> _requestReply = requestReply;

    private AppSettings _settings => _box.Current;

    private void QueueResearch(BotConversation conversation, ToolCall call, IToolBinding binding,
        AppSettings snapshot, EffectivePlatformPolicy platformPolicy, ChatToolCatalog catalog)
    {
        var key = conversation.SourceKey;
        var search = call.ToolId == "web.search";
        bool Allowed() => binding.IsReady && _replyTools.Allows(snapshot, platformPolicy, call.ToolId, catalog)
            && _replyTools.Allows(_settings, platformPolicy, call.ToolId, catalog);
        if (!Allowed()) return;
        try
        {
            if (search && !binding.GetContribution<IResearchUseCase>()
                .TryBeginSearch(key, snapshot.WebSearchCooldownSeconds, out _)) return;
        }
        catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable")
        {
            return;
        }
        var value = call.Arguments[search ? "query" : "url"]!.GetValue<string>();
        var job = (search ? "web-search:" : "web-read:") + key + ":" + value;
        _queueBackground(job, job, async ct =>
        {
            if (ct.IsCancellationRequested || !Allowed()) return;
            try
            {
                var outcome = await binding.ExecuteAsync(key, call, ct);
                if (ct.IsCancellationRequested || !Allowed()) return;
                if (!outcome.Ok && (!search || outcome.ReasonCode != "research_unavailable"))
                    _participation.Observe(key, ParticipationEvent.ToolFailure);
                _requestReply(conversation, snapshot);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException error) when (error.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable") { }
            catch (Exception error)
            {
                _hooks.Log($"[Search] 工具执行失败: {error.GetType().Name}");
                _participation.Observe(key, ParticipationEvent.ToolFailure);
            }
        });
    }

    /// <summary>
    /// 第 4 步的**动作**那一半：搜索 / 读页 / 听歌 / 分享歌（都是两轮动作，后台去跑、下一轮再开口）
    /// 加上表情包候选的校验（不在库里、没过审、频率门都要挡掉）。
    /// 每个动作都要过服务端能力闸门（P3：模型输出不构成授权）；返回这一轮真正要发的表情包（可能为 null）。
    /// </summary>
    public async Task<StickerRecord?> QueueTurnActions(
         BotConversation conversation,
         AppSettings snapshot,
         Domain.Permissions.ChatCapabilitySet caps,
         EffectivePlatformPolicy platformPolicy,
         CompletionResult result,
        bool directAddress)
    {
        await Task.CompletedTask;
        if (_riskBackoff?.IsActive(conversation.SourceKey) == true)
        {
            _hooks.Log($"协议端风控退避中，暂停语音、表情包、音乐和主动动作（会话={Channels.Describe(conversation.SourceKey)}）");
            return null;
        }

        // 表情包：模型可以只发图不说话，也可以“文字 + 图”。
        // 校验一下 id（模型偶发会编造/多空格），拿不到就把这次当成纯文字。
        StickerRecord? sticker = null;
        // 联网搜索（search / read）：后台去查，拿到结果后再给它一次开口的机会。
        // 这两个是“两轮动作”—— 模型这轮照常接话（reply 可以写“我去查查”），下一轮拿着事实说。
        // search 优先于 read：模型一般只会填一个。
        var search = result.Search is { Length: > 0 };
        var requested = search ? result.Search : result.Read;
        if (requested is { Length: > 0 } && _research.IsReady)
        {
            var id = search ? "web.search" : "web.read";
            var call = new ToolCall(id, new JsonObject { [search ? "query" : "url"] = requested });
            var binding = caps.Catalog.FindBinding(id);
            if (binding is not null && _replyTools.Allows(snapshot, platformPolicy, id, caps.Catalog)
                && ToolArguments.Validate(binding.Definition, call.Arguments).Ok
                && _approvals.AllowCapability(conversation, id, requested, out _, pinned: caps))
            {
                await ReplyCommitBoundary.BeforeEffectAsync();
                QueueResearch(conversation, call, binding, snapshot, platformPolicy, caps.Catalog);
            }
        }

        // 模型想听一首歌（listen 字段）：后台去搜、去听，听完再给它一次开口的机会。
        // 这是群里说“去听一下 XXX”的唯一入口 —— 不靠正则猜句子，交给模型自己决定。
        // P3（V3 §9.2）：听歌是“真出网”（去外部音乐服务搜歌 + 拉音频），必须过能力闸门 ——
        // 不能因为它是“老入口”就绕过场景白名单与预算。
        if (platformPolicy.Feature("music", snapshot.EnableMusic).Enabled && _allowsFeature(PluginGate.MusicCapability) && result.Listen is { Length: > 0 } wantedSong && _music is not null
            && _approvals.AllowCapability(conversation, "music.listen", wantedSong, out _, pinned: caps))
        {
            var key = conversation.SourceKey;
            await ReplyCommitBoundary.BeforeEffectAsync();
            if (_music.TryBeginListen(key, snapshot.MusicListenCooldownSeconds, out var listenWhy))
            {
                _hooks.Log($"[Music] 模型想听「{wantedSong}」");
                _queueBackground("music-listen:" + key + ":" + wantedSong, "music-listen:" + key + ":" + wantedSong, async ct =>
                {
                    if (!MusicStillAllowed(platformPolicy, "听歌")) return;
                    string? note;
                    try { note = await _music.ListenAsync(key, wantedSong, "群友", ct); }
                    catch (InvalidOperationException ex) when (ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing")
                    {
                        note = null;
                    }
                    if (ct.IsCancellationRequested) return;
                    if (string.IsNullOrWhiteSpace(note))
                    {
                        // P1 观测：模型点名的“听歌”没做成 → 工具失败
                        _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                        return;
                    }

                    _requestReply(conversation, snapshot);
                });
            }
            else
            {
                _hooks.Log($"[Music] 「{wantedSong}」还在冷却中（{listenWhy}）");
            }
        }

        // 模型想把某首歌分享给群里 → 搜到就发一张网易云卡片，顺手“听”一遍（下一轮它就能聊这首歌）。
        // P3（V3 §9.2）：分享歌曲 = 往当前会话发额外消息，必须过同一条闸门（未配场景时行为不变）。
        if (platformPolicy.Feature("music", snapshot.EnableMusic).Enabled && _allowsFeature(PluginGate.MusicCapability) && result.ShareSong is { Length: > 0 } songToShare && _music is not null
            && _approvals.AllowCapability(conversation, "music.share", songToShare, out _, pinned: caps))
        {
            var key = conversation.SourceKey;
            await ReplyCommitBoundary.BeforeEffectAsync();
            var (shareIsGroup, shareTargetId) = conversation.Target;
            _queueBackground("music-share:" + key + ":" + songToShare, "music-share:" + key + ":" + songToShare, async ct =>
            {
                if (!MusicStillAllowed(platformPolicy, "分享")) return;
                SendResult shared;
                try { shared = await _music.ShareWithReceiptAsync(songToShare, shareIsGroup, shareTargetId, ct); }
                catch (InvalidOperationException ex) when (ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing")
                {
                    shared = new(false);
                }
                if (!ReplyDeliveryResults.Confirmed(shared))
                {
                    _participation.Observe(conversation.SourceKey, Services.Participation.ParticipationEvent.ToolFailure);
                    return;
                }

                // 卡片发出去了，接着真去听一遍：下一轮发言时它就“听过这首歌”。
                // 第二个副作用开始前再复核一次：排队/分享期间插件可能被关、会话可能已停机。
                if (!MusicStillAllowed(platformPolicy, "分享后听歌")) return;
                try { _ = await _music.ListenAsync(key, songToShare, "（自己分享的）", ct); }
                catch (InvalidOperationException ex) when (ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing") { }
            });
        }

        if (platformPolicy.Feature("stickers", snapshot.EnableStickers).Enabled && _allowsFeature(PluginGate.StickersCapability) && result.StickerId is { } sid && !_vibes.IsSober(conversation.SourceKey)
            && _approvals.AllowCapability(conversation, "sticker.send", null, out _, pinned: caps))
        {
            sticker = _stickers.Store.Find(sid);
            if (sticker is null)
            {
                _hooks.Log($"模型挑的表情包 #{sid} 不在库里，已忽略（只发文字）");
            }
            else if (sticker.IsSticker != true)
            {
                // 还没通过“是不是表情包”审核的图不当表情包用：
                // 宁可这一轮不发，也不要把聊天截图/广告发出去
                _hooks.Log($"这张 #{sid} 还没通过“是不是表情包”审核，本轮不发");
                sticker = null;
            }
            else if (!_delivery.AllowSticker(conversation, sticker.Id, out var stickerWhy))
            {
                // 频率门：库小的时候模型会每句都挂同一张（群里直接开愤：
                // “你别老是发这个表情包了”）。表情包是调味品，不是主食。
                _hooks.Log($"这次不发表情包（{stickerWhy}）: #{sticker.Id} {StickerText.Describe(sticker)}");
                sticker = null;
            }
        }
        return sticker;
    }

    /// <summary>
    /// 后台音乐作业开始（以及串起第二个副作用）之前的实时复核：
    /// 插件、功能、平台与会话都必须仍然有效，排队期间被关掉的作业不能再发起新的出网/发送。
    /// </summary>
    private bool MusicStillAllowed(EffectivePlatformPolicy turnPolicy, string phase)
    {
        if (!_allowsFeature(PluginGate.MusicCapability) || _music is null)
        {
            _hooks.Log($"[Music] 后台{phase}已取消（插件或服务已关闭）");
            return false;
        }

        // 用本轮的真实平台上下文重新解析：不能用 channel-only 猜测，
        // 新配置下 channel 没有实例身份，会被判成 source_context_required。
        if (PlatformSourceExecution.Current is { IsCurrent: false })
        {
            _hooks.Log($"[Music] 后台{phase}已取消（来源已退休）");
            return false;
        }

        var policy = _platformPolicies.Resolve(turnPolicy.Context);
        var enabled = policy.Registered && policy.Connected && policy.Enabled && policy.ChatEnabled
            && policy.Feature("music", _settings.EnableMusic).Enabled;
        if (!enabled)
        {
            _hooks.Log($"[Music] 后台{phase}已取消（平台或功能已关闭）");
        }

        return enabled;
    }
}
