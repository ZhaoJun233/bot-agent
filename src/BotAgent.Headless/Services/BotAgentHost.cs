using BotAgent.Domain.Conversation;
using BotAgent.Domain.Ports;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Poke;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;

namespace BotAgent.Services;

/// <summary>
/// 机器人主控（headless 版 Agent 循环；类名带 Host：根命名空间已叫 <c>BotAgent</c>，同名类型会遮蔽它）：
/// **只管"一次回复这件事"的宿主那一侧** —— 订阅消息源、把入站消息交给 <see cref="ReplyPipeline" />、
/// 确认登录身份，以及代发（面板输入框那一路）。
/// 一次回复的完整链路都从它这里过：
/// 白名单过滤 → 落会话 → 写人物档案 → 冷却判断 → 串行请求模型 → 分句发送 → 持久化。
///
/// 它以前还兼着三份差事，批次 4 / 批次 5 各自搬走了（这就是它从 6295 行缩到几百行的原因）：
///   • **面板 façade**：会话列表 / 心情 / 表情包 / agent 会话 / 参与快照 / 配置保存…
///     → 面板直接注入各用例（见 <c>Adapters/Panel</c>）；
///   • **配置热更新的扇出** → <see cref="Settings.SettingsHotReload" />；
///   • **健康日报要的那几个计数** → 日报直连组件（不再经过这里）。
///
/// 运行方式带来的三处取舍：
///   • 没有 UI 线程与 DispatcherQueue，全部在后台线程；并发由锁与串行 worker 保证。
///   • AI 模式由配置控制（容器里没有"AI 开 / 关"按钮）。
///   • 群历史在首次收到该群消息时拉取（没有会话列表可点）。
/// </summary>
public sealed class BotAgentHost : IDisposable
{
    /// <summary>上行通道（私域 + 可选的官方）：入站事件从这里来，代发也从这里出去。</summary>
    private readonly IQqChatSource _source;

    private readonly IModelClient _brain;

    /// <summary>面板事件聚合（日志 / 思考态 / 五类事件都从这里出去）—— 面板直接订阅它，这里不再转发。</summary>
    private readonly PanelNotifier _ui;

    /// <summary>会话注册表（内存列表 + 落库/恢复都在它里面，见 <see cref="ConversationRegistry" />）。</summary>
    private readonly ConversationRegistry _registry;

    /// <summary>回复主链（入站 → 排队 → 取上下文 → 调模型 → 动作 → 发送记账）。</summary>
    private readonly ReplyPipeline _reply;

    /// <summary>后台巡检（静默兜底 / 画像巡检 / 表情包巡检 / 账号在线探测）。</summary>
    private readonly BotScheduler _scheduler;

    /// <summary>戳一戳（状态 + 流程），见 <see cref="PokeUseCase" />。</summary>
    private readonly PokeUseCase _poke;

    /// <summary>
    /// 表情包运营（收图/说明/自巡检）：这里只用它的**收摊**（<see cref="StickerService.Dispose" />）。
    /// 载入归装配点（建好即就位），运行期的收图/巡检归回复链与 <see cref="BotScheduler" />。
    /// </summary>
    private readonly StickerService _stickers;

    /// <summary>登录的 QQ 号（协议端上报优先，其次配置）—— 共享状态，见 <see cref="BotIdentity" />。</summary>
    private readonly BotIdentity _identity;

    /// <summary>是否已经在收摊（定时器/兜底都问它），见 <see cref="BotLifetime" />。</summary>
    private readonly BotLifetime _lifetime;

    public BotAgentHost(
        IQqChatSource source,
        IModelClient brain,
        PanelNotifier ui,
        ConversationRegistry registry,
        ReplyPipeline reply,
        BotScheduler scheduler,
        PokeUseCase poke,
        StickerService stickers,
        BotIdentity identity,
        BotLifetime lifetime)
    {
        // 这里**只做赋值**：谁依赖谁、谁先谁后，全在 Host/CompositionRoot 那一处说了算（§6.1）。
        // 以前这个构造函数自己 new 了十几种具体实现，想知道"谁真正拥有音乐服务"得读两个文件。
        _source = source;
        _brain = brain;
        _ui = ui;
        _registry = registry;
        _reply = reply;
        _scheduler = scheduler;
        _poke = poke;
        _stickers = stickers;
        _identity = identity;
        _lifetime = lifetime;
    }

    /// <summary>
    /// 启动：订阅消息源、恢复磁盘会话、启动后台巡检的定时器。
    /// 不在这里做的事：表情包库/心情的**载入**（那是装配点的启动顺序，见 CompositionRoot）与启动自述
    /// （见 <see cref="BootReport" />）—— 它们只需要"图里有哪些件"，和"一次回复怎么走"没关系。
    /// </summary>
    public void Start()
    {
        _source.MessageReceived += OnMessageReceived;
        _source.ConnectionChanged += OnConnectionChanged;
        _source.Poked += OnPoked;
        _source.MessageRecalled += _reply.OnMessageRecalled;

        RestoreConversations();

        // 定时器（静默兜底 / 画像巡检 / 表情包巡检）+ QQ 账号在线探测都在 BotScheduler 里
        _scheduler.Start();
    }

    public void Dispose()
    {
        if (!_lifetime.MarkDisposed())
        {
            return;
        }

        _source.MessageReceived -= OnMessageReceived;
        _source.ConnectionChanged -= OnConnectionChanged;
        _source.Poked -= OnPoked;
        _source.MessageRecalled -= _reply.OnMessageRecalled;
        _scheduler.Stop();
        _stickers.Dispose();
    }

    /// <summary>以机器人身份向会话发送一条消息（面板输入框那一路）。</summary>
    public async Task<bool> SendAsBotAsync(string sourceKey, string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var conversation = _registry.Find(sourceKey);
        if (conversation is null)
        {
            return false;
        }

        var (isGroup, targetId) = conversation.Target;
        if (targetId == 0 || !_source.IsConnected)
        {
            return false;
        }

        var sent = await _source.SendTextAsync(isGroup, targetId, text);
        if (sent.Ok)
        {
            conversation.Append(new ChatMessage { Role = MessageRole.Self, Text = text, Timestamp = Clock.Now, QqMessageId = sent.MessageId > 0 ? sent.MessageId : null });
            _registry.Touch(conversation);
            _ui.NotifyMessageAdded(conversation.SourceKey, conversation.Messages[^1]);
            _registry.Save();
        }

        return sent.Ok;
    }

    // ---------- 连接与自我识别 ----------

    private void OnConnectionChanged(bool connected)
    {
        EmitLog(connected ? "QQ 已连接（协议端在线）" : "QQ 连接断开，等待重连…");

        if (!connected)
        {
            // 连接都没了，账号在线状态回到未知，等重连后再探
            _scheduler.ResetAccountState();
        }

        _ui.NotifyStateChanged();
        if (connected)
        {
            _ = RefreshSelfIdAsync();
        }
    }

    /// <summary>连接后向协议端确认登录号，用于 @ 识别与身份注入。</summary>
    private async Task RefreshSelfIdAsync()
    {
        try
        {
            if (_source is not OneBotGateway gateway)
            {
                return;
            }

            // 给协议端一点握手时间（NapCat 刚连上时 get_login_info 可能还没就绪）
            await Clock.Delay(1500);
            var id = await gateway.GetSelfIdAsync();
            if (id is > 0)
            {
                _identity.Set(id.Value);
                gateway.SelfIdHint = id.Value;
                _brain.BotIdentity = id.Value.ToString();
                EmitLog($"登录账号确认：QQ {id.Value}");
            }

            // 紧接一次账号在线探测：连接刚建立时就能发现“连上了但账号已掉线”
            await _scheduler.CheckAccountOnlineAsync();
        }
        catch (Exception ex)
        {
            EmitLog("获取登录账号失败: " + ex.Message);
        }
    }

    // ---------- 入站消息 ----------

    private void OnMessageReceived(QqChatMessage msg)
    {
        try
        {
            _reply.HandleInbound(msg);
        }
        catch (Exception ex)
        {
            EmitLog("处理入站消息异常: " + ex.Message);
        }
    }

    // ══════════ 戳一戳（状态与流程都在 PokeUseCase，这里只接线） ══════════

    private void OnPoked(QqPokeEvent poke) => _poke.OnPoked(poke);

    // ---------- 会话管理 ----------

    private void RestoreConversations()
    {
        _registry.Restore();

        // 恢复出来的会话，历史按“还没拉过”算（首次收到消息时才去拉群历史，避免启动就把所有群刷一遍）
        foreach (var conversation in _registry.Snapshot())
        {
            _reply.MarkHistoryPending(conversation.SourceKey);
        }
    }

    // ══════════ 内部辅助 ══════════

    /// <summary>写文件日志，并推送给 Web UI 日志面板（实现见 <see cref="PanelNotifier.EmitLog" />）。</summary>
    private void EmitLog(string message) => _ui.EmitLog(message);
}
