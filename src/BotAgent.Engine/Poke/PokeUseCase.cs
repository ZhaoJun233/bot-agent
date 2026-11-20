using BotAgent.Engine.Runtime;
using BotAgent.Services.OneBot;
using BotAgent.Domain.Conversation;
using BotAgent.Services.Conversations;
using BotAgent.Services.Plugins;
using System.Collections.Concurrent;

namespace BotAgent.Services.Poke;

/// <summary>
/// 戳一戳用例：宿主通道入口与会话桥接。核心冷却/最近戳人/反刷屏回戳策略由 <c>preset.feature.poke</c>
/// 独立包经 <see cref="IPokePolicy" /> 贡献；缺包或禁用时 fail-closed，不产生行为。
/// </summary>
public sealed class PokeUseCase
{
    public const string ProviderId = "preset.feature.poke";

    private readonly ISettingsBox _box;
    private readonly PokeHooks _hooks;
    private readonly PluginManager? _plugins;
    private readonly IPokePolicy? _directPolicy;

    public Action<BotConversation>? RequestReply { get; set; }

    /// <summary>Plugin gate hook: disabled preset plugin must not process new poke events.</summary>
    public Func<bool>? PluginEnabled { get; set; }

    public PokeUseCase(ISettingsBox box, PokeHooks hooks, PluginManager? plugins = null, IPokePolicy? directPolicy = null)
    {
        _box = box;
        _hooks = hooks;
        _plugins = plugins;
        _directPolicy = directPolicy;
    }

    private AppSettings _settings => _box.Current;

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private IPokePolicy? CurrentPolicy
        => _directPolicy ?? (_plugins is not null && _plugins.TryGetContribution<IPokePolicy>(ProviderId, out var policy)
            ? policy : null);

    /// <summary>收到戳一戳（在协议端线程上跑）：异常不许逃出去 —— 处理不了顶多是不回，不能带崩接收循环。</summary>
    public void OnPoked(QqPokeEvent poke)
    {
        if (PluginEnabled?.Invoke() == false) return;
        try
        {
            if (CurrentPolicy is null) return;
            HandlePoke(poke);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            // 插件并发停用/重载：静默 fail-closed，不带崩协议端接收循环。
        }
        catch (Exception ex)
        {
            _hooks.Log("处理戳一戳事件异常: " + ex.Message);
        }
    }

    private void HandlePoke(QqPokeEvent poke)
    {
        if (!_settings.EnablePoke)
        {
            return;
        }

        var policy = CurrentPolicy;
        if (policy is null)
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
            0, poke.IsGroup, poke.UserId, poke.GroupId, string.Empty, string.Empty, poke.Time, false) { Channel = poke.Channel });
        if (conversation is null)
        {
            return;
        }

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
            QqMessageId = 0,
            DirectToBot = poke.IsSelfPoked
        };
        _hooks.RecordInbound(conversation, appended);

        _hooks.Log($"收到戳一戳：{(poke.IsGroup ? $"群{poke.GroupId}" : "私聊")} {pokerName}({poke.UserId}) → " +
                   $"{(poke.IsSelfPoked ? "机器人" : DisplayNames.Of(conversation, poke.TargetId, _hooks.SelfId()))}({poke.TargetId})");

        if (!poke.IsSelfPoked)
        {
            return; // 别人互戳只进上下文
        }

        var now = EngineClock.Now;
        var decision = policy.EvaluateInbound(conversation.SourceKey, poke.UserId, _settings.PokeCooldownSeconds, now);
        if (!decision.ShouldReply)
        {
            _hooks.Log($"同一个人的连续戳 → 这次不回应（{decision.RetryAfterSeconds:F0}s 后放行）: {conversation.Name}");
            return;
        }

        if (_plugins is null)
        {
            // 仅独立测试模式由宿主回调记账；运行期由 preset.feature.poke 通过 IPokeInteractionFactSink 通知。
            _hooks.RecordPokeMood(now);
        }

        // 戳一戳没有消息 id，引用目标交给模型自己用 replyTo 指认
        RequestReply?.Invoke(conversation);
    }

    public bool RecentlyPoked(string sourceKey, TimeSpan within, DateTimeOffset now)
    {
        try { return CurrentPolicy?.RecentlyPoked(sourceKey, within, now) == true; }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return false; }
    }

    public bool IsRecentPoker(string sourceKey, long userId)
    {
        try { return CurrentPolicy?.IsRecentPoker(sourceKey, userId) == true; }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return false; }
    }

    public bool AllowPokeBack(string sourceKey, long targetId, out string reason)
    {
        var policy = CurrentPolicy;
        if (policy is null)
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }

        try { return policy.AllowPokeBack(sourceKey, targetId, _settings.PokeCooldownSeconds, EngineClock.Now, out reason); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }
    }

    public void NotePokedBack(string sourceKey, long targetId, DateTimeOffset now)
    {
        try { CurrentPolicy?.NotePokedBack(sourceKey, targetId, now); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public void ClearConversation(string sourceKey)
    {
        try { CurrentPolicy?.ClearConversation(sourceKey); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }
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
    Func<QqChatMessage, BotConversation?> GetOrCreateConversation,
    Action<BotConversation, ChatMessage> RecordInbound,
    Action<string, string> LogThrottled,
    Action<string> Log,
    Action<DateTimeOffset> RecordPokeMood);
