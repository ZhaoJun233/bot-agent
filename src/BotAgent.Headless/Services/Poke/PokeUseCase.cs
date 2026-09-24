using BotAgent.Services.OneBot;
using BotAgent.Domain.Conversation;
using BotAgent.Services.Conversations;
using System.Collections.Concurrent;

namespace BotAgent.Services.Poke;

/// <summary>
/// 戳一戳用例：**状态**（最近戳了机器人的人 / 最近主动戳过谁）+ **判定**（要不要回应、能不能戳回去）+ **流程**。
///
/// 分成两半看：
///   • 进（别人戳我们）：记进上下文 → 同一人连续戳时冷却内只回一次 → 请求一次回复；
///     别人互戳**只记上下文不插话**（群里互相戳得很多，每条都回就是刷屏）。
///   • 出（模型想戳回去）：号码必须真的在本次上下文里出现过（防编造），再过心情门、频率门、能力闸门 ——
///     这三道门在回复链里查，用 <see cref="IsRecentPoker" /> / <see cref="AllowPokeBack" /> / <see cref="NotePokedBack" />。
///
/// 宿主能力（会话、显示名、记账、请求回复、心情）从 <see cref="PokeHooks" /> 注入 —— 方向是「用例 → 宿主」，
/// 所以这里不持有 BotAgentHost，也不认识它的内部状态。
/// </summary>
public sealed class PokeUseCase
{
    private readonly SettingsBox _box;
    private readonly PokeHooks _hooks;

    /// <summary>最近一次“戳了机器人”的人（用来校验模型想戳回去的号码是否真的存在）。</summary>
    private readonly ConcurrentDictionary<string, (long PokerId, DateTimeOffset At)> _lastPoke = new();

    /// <summary>每个会话最近一次主动戳人（时间 + 戳的是谁）：用于频率门与“同一个人不反复戳”。</summary>
    private readonly ConcurrentDictionary<string, (long TargetId, DateTimeOffset At)> _lastPokeSent = new();

    /// <summary>
    /// 请求一轮回复的入口：由**装配点**在建好回复主链之后接上 ——
    /// 戳一戳要"请求一轮回复"，而主链本身又要用戳一戳的状态（<see cref="IsRecentPoker" /> 等），
    /// 只有这一个方向需要延迟接线（其余能力都在 <see cref="PokeHooks" /> 里一次性给全）。
    /// </summary>
    public Action<BotConversation>? RequestReply { get; set; }

    public PokeUseCase(SettingsBox box, PokeHooks hooks)
    {
        _box = box;
        _hooks = hooks;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>收到戳一戳（在协议端线程上跑）：异常不许逃出去 —— 处理不了顶多是不回，不能带崩接收循环。</summary>
    public void OnPoked(QqPokeEvent poke)
    {
        try
        {
            HandlePoke(poke);
        }
        catch (Exception ex)
        {
            _hooks.Log("处理戳一戳事件异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 戳一戳怎么处理：
    ///   • 戳了机器人 → 记进上下文（模型才看得到），并触发一次回复；同一个人连着戳时冷却内只回一次。
    ///   • 别人互戳 → 只记进上下文，**不**主动插话。
    /// 模型回答时可选地在 JSON 里给 poke 字段（对方 QQ 号）戳回去，安全阀在回复链里查（见本类开头）。
    /// </summary>
    private void HandlePoke(QqPokeEvent poke)
    {
        if (!_settings.EnablePoke)
        {
            return;
        }

        // 自己戳的（协议端可能回显）不管
        var selfId = _hooks.SelfId();
        if (selfId != 0 && poke.UserId == selfId)
        {
            return;
        }

        var sourceId = poke.IsGroup ? poke.GroupId : poke.UserId;
        if (!_hooks.IsSourceAllowed(poke.IsGroup, sourceId))
        {
            _hooks.LogThrottled("poke-ignore:" + sourceId,
                $"忽略戳一戳（不在白名单）: {(poke.IsGroup ? "群 " + poke.GroupId : "私聊 " + poke.UserId)}");
            return;
        }

        var conversation = _hooks.GetOrCreateConversation(new QqChatMessage(
            0, poke.IsGroup, poke.UserId, poke.GroupId, string.Empty, string.Empty, poke.Time, false));

        // 名字尽量从历史里找（notice 事件本身不带昵称/群名片）
        var pokerName = DisplayNames.Of(conversation, poke.UserId, _hooks.SelfId());
        var text = poke.IsSelfPoked
            ? $"（戳一戳）{pokerName} 戳了你一下"
            : $"（戳一戳）{pokerName} 戳了 {DisplayNames.Of(conversation, poke.TargetId, _hooks.SelfId())} 一下";

        var appended = new ChatMessage
        {
            Role = MessageRole.Peer,
            SenderName = pokerName,
            SenderId = poke.UserId,
            Text = text,
            Timestamp = poke.Time,
            QqMessageId = 0
        };
        _hooks.RecordInbound(conversation, appended);

        _hooks.Log($"收到戳一戳：{(poke.IsGroup ? $"群{poke.GroupId}" : "私聊")} {pokerName}({poke.UserId}) → " +
                   $"{(poke.IsSelfPoked ? "机器人" : DisplayNames.Of(conversation, poke.TargetId, _hooks.SelfId()))}({poke.TargetId})");

        if (!poke.IsSelfPoked)
        {
            return; // 别人互戳只进上下文
        }

        var now = Clock.Now;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.PokeCooldownSeconds));
        if (cooldown > TimeSpan.Zero &&
            _lastPoke.TryGetValue(conversation.SourceKey, out var last) &&
            last.PokerId == poke.UserId &&
            now - last.At < cooldown)
        {
            _hooks.Log($"同一个人的连续戳 → 这次不回应（{cooldown.TotalSeconds - (now - last.At).TotalSeconds:F0}s 后放行）: {conversation.Name}");
            return;
        }

        _lastPoke[conversation.SourceKey] = (poke.UserId, now);
        // 心情的客观来源：被戳的次数（越频繁越烦，也会随时间自己消）
        _hooks.RecordPokeMood(now);

        // 戳一戳没有消息 id，引用目标交给模型自己用 replyTo 指认
        RequestReply?.Invoke(conversation);
    }

    /// <summary>最近（<paramref name="within" /> 内）被戳过？—— 决定提示词里要不要给“可以戳回去”的指令。</summary>
    public bool RecentlyPoked(string sourceKey, TimeSpan within, DateTimeOffset now)
        => _lastPoke.TryGetValue(sourceKey, out var last) && now - last.At < within;

    /// <summary>这个号码是不是“刚戳过机器人的人”—— 模型想戳回去时，它也算“确实出现过”（防编造号码）。</summary>
    public bool IsRecentPoker(string sourceKey, long userId)
        => _lastPoke.TryGetValue(sourceKey, out var last) && last.PokerId == userId;

    /// <summary>
    /// 主动戳人的频率门：① 距上次戳人至少 <c>PokeCooldownSeconds</c>；
    /// ② 同一个人 5 分钟内不反复戳（模型很容易顺着“你戳我我戳你”一直戳下去，群里看着就是刷屏）。
    /// </summary>
    public bool AllowPokeBack(string sourceKey, long targetId, out string reason)
    {
        reason = string.Empty;
        if (!_lastPokeSent.TryGetValue(sourceKey, out var last))
        {
            return true;
        }

        var since = Clock.Now - last.At;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, _settings.PokeCooldownSeconds));
        if (cooldown > TimeSpan.Zero && since < cooldown)
        {
            reason = $"距上次戳人才 {since.TotalSeconds:F0}s（下限 {cooldown.TotalSeconds:F0}s）";
            return false;
        }

        // 同一个人不反复戳
        if (last.TargetId == targetId && since < TimeSpan.FromMinutes(5))
        {
            reason = $"{since.TotalSeconds:F0}s 前刚戳过这个人";
            return false;
        }

        return true;
    }

    /// <summary>记下这次主动戳人（只有真发出去了才记 —— 协议端不支持时不该占掉冷却）。</summary>
    public void NotePokedBack(string sourceKey, long targetId, DateTimeOffset now)
        => _lastPokeSent[sourceKey] = (targetId, now);
}

/// <summary>
/// 戳一戳用例要用到的宿主能力（由 BotAgentHost 提供实现）。
/// 全是回调而不是接口实现，是为了让宿主那边**一个方法都不新增**：接线只写在构造函数的参数里。
/// </summary>
/// <param name="SelfId">登录的 QQ 号（0 = 未知）。</param>
/// <param name="IsSourceAllowed">这一条来源（群/私聊）是否在白名单里。</param>
/// <param name="GetOrCreateConversation">按事件里的通道/群号建会话（没有就新建）。</param>
/// <param name="RecordInbound">把这条系统消息记进上下文（追加 → 重排 → 通知面板 → 存盘）。</param>
/// <param name="LogThrottled">节流日志（忙群里同一来源每分钟最多一条）。</param>
/// <param name="Log">普通运行日志（写文件 + 推面板）。</param>
/// <param name="RecordPokeMood">被戳一次（心情的客观来源）。</param>
public readonly record struct PokeHooks(
    Func<long> SelfId,
    Func<bool, long, bool> IsSourceAllowed,
    Func<QqChatMessage, BotConversation> GetOrCreateConversation,
    Action<BotConversation, ChatMessage> RecordInbound,
    Action<string, string> LogThrottled,
    Action<string> Log,
    Action<DateTimeOffset> RecordPokeMood);
