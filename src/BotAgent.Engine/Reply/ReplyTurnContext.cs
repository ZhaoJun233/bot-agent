using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Stickers;
using BotAgent.Engine.Runtime;
using BotAgent.Services.Conversations;
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Permissions;
using BotAgent.Services.Plugins;
using BotAgent.Services.Poke;
using BotAgent.Services.Qq;
using BotAgent.Services.Stickers;

namespace BotAgent.Services.Reply;

/// <summary>
/// 轮次上下文构建（#106 拆分第 5 步）：把"这一轮模型能看见什么"凑齐 —— 会话窗口、参与者档案、
/// 表情包候选、群成员身份、上一轮气氛、心情、刚听过的歌、刚撤回的消息、刚查到的资料、链接预览。
///
/// 为什么单独成类：这一段**只读不写、不下判断、不发任何东西**（判断在回复执行里）。
/// 它的产出 <see cref="TurnInputs" /> 就是提示词的全部素材；出了"模型突然看不见某样东西"的问题时，
/// 排查范围只剩这一个类，而不是整条回复主链。
///
/// 两个约定：
/// - 素材里凡是"用完就清"的（歌、撤回、资料、链接），取走即清，避免以后每一轮都背上它；
/// - 链接预览只等 2.5 秒（快站点能赶上当轮，慢了留给下一轮），不能为了它把回复延迟拉长。
/// </summary>
internal sealed class ReplyTurnContext
{
    private readonly IProfileRepository _profiles;
    private readonly Func<PlatformSourceLease, long, BotAgent.Domain.Messaging.ParticipantId?>? _memoryParticipant;
    private readonly VibeTracker _vibes;
    private readonly StickerService _stickers;
    private readonly MemberRoleUseCase _roles;
    private readonly PokeUseCase _poke;
    private readonly IMoodRepository _mood;
    private readonly IMusicUseCase _music;
    private readonly IResearchUseCase _research;
    private readonly ReplyInboundMedia _media;
    private readonly ReplyHooks _hooks;
    private readonly Func<string, bool> _allowsFeature;
    private readonly Action<BotConversation, bool> _setThinking;

    /// <summary>每个会话最近一次撤回事件的描述，交给下一轮回复用（用掉就清）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _recallNotes = new();

    public ReplyTurnContext(
        IProfileRepository profiles,
        VibeTracker vibes,
        StickerService stickers,
        MemberRoleUseCase roles,
        PokeUseCase poke,
        IMoodRepository mood,
        IMusicUseCase music,
        IResearchUseCase research,
        ReplyInboundMedia media,
        ReplyHooks hooks,
        Func<string, bool> allowsFeature,
        Action<BotConversation, bool> setThinking,
        Func<PlatformSourceLease, long, BotAgent.Domain.Messaging.ParticipantId?>? memoryParticipant = null)
    {
        _profiles = profiles;
        _memoryParticipant = memoryParticipant;
        _vibes = vibes;
        _stickers = stickers;
        _roles = roles;
        _poke = poke;
        _mood = mood;
        _music = music;
        _research = research;
        _media = media;
        _hooks = hooks;
        _allowsFeature = allowsFeature;
        _setThinking = setThinking;
    }

    /// <summary>记下“刚有人撤回了一条消息”，下一轮交给模型（取走即清）。</summary>
    public void RememberRecall(string sourceKey, string note) => _recallNotes[sourceKey] = note;

    /// <summary>
    /// 第 1 步：**取上下文**（以及提示词要用的素材）—— 会话窗口、参与者档案、表情包候选、群成员身份、
    /// 上一轮气氛、心情、刚听过的歌、刚撤回的消息、刚查到的资料、链接预览。
    /// 只读不写：这里不下任何判断（判断在第 3 步），也不发任何东西。
    /// </summary>
    public async Task<TurnInputs?> BuildAsync(
        BotConversation conversation,
        AppSettings snapshot,
        Domain.Permissions.ChatCapabilitySet caps,
        EffectivePlatformPolicy platformPolicy,
        bool proactive,
        IPacedReplyAttempt? pacing = null)
    {
        var context = pacing is null
            ? conversation.TakeLast(snapshot.MaxContextMessages)
            : conversation.Messages.Where(m => m.Seq <= pacing.Sequence).TakeLast(snapshot.MaxContextMessages).ToList();
        if (context.Count == 0)
        {
            return null;   // 上下文空 = 没什么可回的（与改造前一致：直接结束这一轮）
        }

        // 当前上下文最早一条：档案只取比它更早的（按单调序号精确判定，避免时间戳秒级精度误判）
        var contextOldestSeq = context[0].Seq;
        var contextOldestUnix = context[0].Timestamp.ToUnixTimeSeconds();
        var (isGroupScope, scopeGroupId) = conversation.Target;

        // 收集最近 40 条上下文里出现过的发送者 QQ → 读取各自在**本会话**的人物档案（角色卡片）
        var profiles = new List<string>();
        var profileChars = 0;
        var recentSenders = _allowsFeature(PluginGate.ProfilesCapability)
            ? context
                .TakeLast(40)
                .Where(m => m.Role == MessageRole.Peer && m.SenderId.HasValue)
                .Select(m => m.SenderId!.Value)
                .Distinct()
                .Take(Math.Max(0, snapshot.ProfileLookupCount))
                .ToList()
            : new List<long>();

        foreach (var uid in recentSenders)
        {
            var participant = PlatformSourceExecution.Current is { } source ? _memoryParticipant?.Invoke(source, uid) : null;
            string ReadProfile(string profileKey) => _profiles.GetProfileSummary(profileKey,
                isGroupScope ? scopeGroupId : 0, snapshot.ProfileSummaryLines, contextOldestSeq, contextOldestUnix);
            var summary = UnifiedMemoryTurn.Current is { } memory
                ? memory.ReadLegacyProfile(participant, ReadProfile) : ReadProfile(uid.ToString());

            if (string.IsNullOrWhiteSpace(summary))
            {
                continue; // 本会话没有更早的历史 → 不占提示词预算
            }

            if (profileChars + summary.Length > snapshot.MaxProfileChars)
            {
                break; // 超出预算：后面的（发言更早的）丢弃
            }

            profiles.Add(summary);
            profileChars += summary.Length;
        }

        var memoryPrompt = UnifiedMemoryTurn.Current?.ReadPrompt(string.Join(" ", context.TakeLast(3).Select(m => m.Text)));
        if (!string.IsNullOrEmpty(memoryPrompt))
        {
            profiles.Add(memoryPrompt);
            profileChars += memoryPrompt.Length;
        }

        var started = EngineClock.LocalDateTime;
        _setThinking(conversation, true);

        // 表情包候选：拿最近的对话文字当检索词，从库里挑几张给模型选。
        // 先挑后发 —— 库可能有上千张，全塞进提示词既贵又不准。
        // 气氛“沉”（有人低落/在吵架）时不给候选：给了它就容易挑一张发出去，与气氛不搭。
        var stickerChoices = new List<StickerChoice>();
        if (platformPolicy.Feature("stickers", snapshot.EnableStickers).Enabled && _allowsFeature(PluginGate.StickersCapability) && snapshot.StickerLibraryMax > 0 && snapshot.StickerCandidates > 0 &&
            !_vibes.IsSober(conversation.SourceKey))
        {
            var query = string.Join(" ", context.TakeLast(8).Select(m => m.Text));
            stickerChoices = _stickers
                .Store
                .PickCandidates(query, snapshot.StickerCandidates, -1, conversation.SourceKey)
                .Select(s => new StickerChoice(s.Id, StickerText.Describe(s)))
                .ToList();
        }

        // 群成员身份（群主 / 管理员 / 群头衔）：只在“有值得说的人”时才给，不占 token。
        // 上限 14 人：再多人就只是个名字列表，既没用又费 token。
        // 先等一下“刚触发的身份查询”：查询就十几毫秒，等它一下，免得第一次说话那轮看不到头衔。
        if (isGroupScope)
        {
            await _roles.WaitForPendingAsync(scopeGroupId, TimeSpan.FromMilliseconds(700));
        }

        var roleCount = 0;
        var groupRoles = isGroupScope ? _roles.DescribeForPrompt(scopeGroupId, 14, out roleCount) : null;

        // 上一次读到的气氛（有 TTL）：给模型当底色，让它接得上
        var vibeHint = _allowsFeature(PluginGate.VibesCapability) ? _vibes.Hint(conversation.SourceKey) : string.Empty;
        var previousVibe = _allowsFeature(PluginGate.VibesCapability) ? _vibes.Current(conversation.SourceKey) : "中性";

        _hooks.Log($"请求模型…（{conversation.Name}，上下文 {context.Count} 条，档案 {profiles.Count} 份/{profileChars} 字" +
                (roleCount > 0 ? $"，身份 {roleCount} 人" : string.Empty) +
                (previousVibe.Length > 0 ? $"，上轮气氛 {previousVibe}" : string.Empty) +
                (proactive ? "，主动开口" : string.Empty) +
                (stickerChoices.Count > 0 ? $"，表情包候选 {stickerChoices.Count} 张" : string.Empty) + "）");

        // 最近被戳过（10 分钟内）才给模型“可以戳回去”的指令，平时不浪费 token
        var pokeContext = platformPolicy.Feature("poke", snapshot.EnablePoke).Enabled && _allowsFeature(PluginGate.PokeCapability) &&
            _poke.RecentlyPoked(conversation.SourceKey, TimeSpan.FromMinutes(10), EngineClock.Now);

        // 心情（被戳次数客观 + 模型主观写的）：影响还戳不戳回去、话多话少。
        // 只在“刚被戳过”或“模型写过心情”时给，平常不浪费 token。
        var moodNow = EngineClock.Now;
        var moodText = pokeContext || _mood.CurrentText(moodNow) is not null ? _mood.Describe(moodNow) : null;

        // 刚“听过”的歌：把歌词与波形实测交给模型，用完就清（避免以后每轮都背上它）
        string? TakeNote(ReplyPacingPromptState.Note note, Func<string?> take) =>
            pacing is null ? take() : pacing.PromptState.TakeOnce(note, take);
        var musicText = TakeNote(ReplyPacingPromptState.Note.Music,
            () => _allowsFeature(PluginGate.MusicCapability) ? _music.TakeNote(conversation.SourceKey) : null);

        // 刚有人撤回了消息：把这件事告诉模型（但不告诉它撤回了什么）——用完就清
        var recallText = TakeNote(ReplyPacingPromptState.Note.Recall,
            () => _recallNotes.TryRemove(conversation.SourceKey, out var pendingRecall) ? pendingRecall : null);

        // 刚上网查到的资料 / 读到的页面正文：交给模型，用完就清
        var searchText = TakeNote(ReplyPacingPromptState.Note.Search,
            () => _allowsFeature(PluginGate.WebSearchCapability) ? _research.TakeNote(conversation.SourceKey) : null);

        // 链接预览：给快站点 2.5 秒的机会当轮用上；太慢就先不等（完成后留给下一轮）
        if (_media.PendingLinkTask(conversation.SourceKey) is { } linkTask)
        {
            await Task.WhenAny(linkTask, EngineClock.Delay(TimeSpan.FromMilliseconds(2500)));
        }

        var linkText = TakeNote(ReplyPacingPromptState.Note.Link, () => _media.TakeLinkNote(conversation.SourceKey));

        return new TurnInputs(
            Started: started, Snapshot: snapshot, Caps: caps, PlatformPolicy: platformPolicy, Context: context, Profiles: profiles,
            ProfileChars: profileChars, StickerChoices: stickerChoices, RoleCount: roleCount,
            GroupRoles: groupRoles, VibeHint: vibeHint, PreviousVibe: previousVibe, PokeContext: pokeContext,
            MoodText: moodText, MusicText: musicText, RecallText: recallText, SearchText: searchText,
            LinkText: linkText, IsPrivateConversation: !isGroupScope);
    }
}
