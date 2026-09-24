using BotAgent.Services.Ports;
using BotAgent.Domain.Ports;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.Ops;
using BotAgent.Services.Panel;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

namespace BotAgent.Services.Settings;

/// <summary>
/// 配置热更新（§6.2 的 SettingsHotReload，批次 5 从 <c>BotAgentHost</c> 搬出来）：
/// 面板保存一次设置之后，把"跟着配置走的那堆派生物"按固定顺序重建一遍 ——
/// 能力策略 → 参与上限 → 白名单（含清理）→ agent 用户名单 → 推给大脑 → 并发闸门 →
/// 会话窗口裁剪 → 定时器重排，最后落盘 + 通知面板。
///
/// 为什么单独一个类：面板想"保存设置"以前必须先拿到 <c>BotAgentHost</c>（那是它作为 façade 的副作用之一），
/// 而这段编排要的十几个依赖又把 <c>BotAgentHost</c> 的字段撑得看不见主线。搬到这里之后两边都松开了：
/// 面板只对着"配置这件事"，<c>BotAgentHost</c> 只对着"一次回复这件事"。
///
/// **顺序是语义**（先重建再裁剪、先推大脑再换闸门），所以整段一起搬、一条都没拆散；
/// 唯一的减法见 <see cref="SyncBrainFields" /> 的注释（图片重签那条通道本来就不随配置变）。
/// </summary>
public sealed class SettingsHotReload
{
    private readonly SettingsBox _box;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly WhitelistGate _whitelist;
    private readonly AgentCommandService _agentCmds;
    private readonly IModelClient _brain;
    private readonly ReplyPipeline _reply;
    private readonly BotScheduler _scheduler;
    private readonly ConversationRegistry _registry;
    private readonly PanelNotifier _ui;
    private readonly ISettingsRepository _settingsRepo;

    public SettingsHotReload(
        SettingsBox box,
        ISettingsRepository settingsRepo,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        WhitelistGate whitelist,
        AgentCommandService agentCmds,
        IModelClient brain,
        ReplyPipeline reply,
        BotScheduler scheduler,
        ConversationRegistry registry,
        PanelNotifier ui)
    {
        _box = box;
        _settingsRepo = settingsRepo;
        _approvals = approvals;
        _participation = participation;
        _whitelist = whitelist;
        _agentCmds = agentCmds;
        _brain = brain;
        _reply = reply;
        _scheduler = scheduler;
        _registry = registry;
        _ui = ui;
    }

    /// <summary>配置读取入口：指向**当前发布版**（热更新是换引用，见 <see cref="SettingsBox" />）。</summary>
    private AppSettings _settings => _box.Current;

    /// <summary>
    /// 运行时改配置：白名单/人设/欲望/阈值/限流等立即生效，并落盘 settings.json。
    /// 模型地址与 OneBot 地址不在运行时生效（属于容器环境变量职责），由 UI 明确标注。
    /// </summary>
    public void ApplyRuntimeSettings(Action<AppSettings> mutate)
    {
        // 在**副本**上改、改完原子换引用：之后就启动的每一轮读到的都是完整的新版，
        // 而正在处理的那一轮继续用它入口取的那份快照（review-findings #4）。
        var next = _box.Apply(mutate);

        // 能力策略快照：改完设置立刻按新开关重建（正在处理的那一轮仍用旧快照，V3 §5.3）
        _approvals.RebuildPolicy();

        // 参与状态机的上下限同样跟随设置（状态保留、只换上限）
        _participation.Rebuild();

        // 白名单重新解析并清理已有会话
        _whitelist.Rebuild();
        PruneNonWhitelisted();

        // Agent 用户白名单（改设置后立即生效；空 = 谁都不能用）
        _agentCmds.RebuildAgentUsers();

        // 同步到模型客户端
        SyncBrainFields();

        // 全局并发数变更时换闸门（旧的不 Dispose：在途的那次还在等它，见 ReplyPipeline.ResizeGate）
        _reply.ResizeGate(Math.Clamp(_settings.MaxConcurrentReplies, 1, 16));

        // 消息窗口变更时同步到已有会话，并立即裁一次（否则要等下一条消息才看得到效果）
        foreach (var c in _registry.Snapshot())
        {
            c.MaxMessages = _settings.MaxMessagesPerConversation;
            c.TrimToMax();
        }

        // 定时器类配置（静默兜底 / 画像巡检）：只在真的改了时才重建，否则不生效
        _scheduler.RebuildIfNeeded();

        _settingsRepo.Save(next);
        _ui.EmitLog("配置已更新");
        _ui.NotifyConversationsChanged();
        _ui.NotifyStateChanged();
    }

    /// <summary>
    /// 只把「当前生效的那一份」里的某个值换掉：**不重建派生物、不落盘**。
    /// 用在"已经在别处持久化过"的运行时值上 —— 例如网易云登录态（存 secrets 表、<c>[JsonIgnore]</c> 不进 settings.json）：
    /// 它既不该触发白名单清理，也不该让面板多出一条"配置已更新"。
    /// </summary>
    public void PatchSettings(Action<AppSettings> mutate) => _box.Apply(mutate);

    /// <summary>
    /// 会话被删除 / 移出白名单时，把它在各台账里的痕迹一起清掉（V3 §7.2 / §9.4：内存台账要有界）。
    /// 审批台账按“已结束的单子”周期清理（<see cref="ApprovalStore.Prune" />），不在这里逐会话删 ——
    /// 审批单可能跨越一次白名单变更，删早了会让正在等待批准的那条悄悄失效。
    /// </summary>
    public void ForgetSessionState(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return;
        }

        _participation.Forget(sourceKey);
        _approvals.Forget(sourceKey);
    }

    /// <summary>把当前发布版里与模型相关的字段推给大脑（身份 / 人设 / 欲望 / 阈值 / 上下文窗口）。</summary>
    private void SyncBrainFields()
    {
        _brain.BotIdentity = string.IsNullOrWhiteSpace(_settings.NormalizedUin) ? null : _settings.NormalizedUin;
        _brain.BotPersona = _settings.BotPersona;
        _brain.AiDesire = _settings.AiDesire;
        _brain.SuitabilityThreshold = _settings.SuitabilityThreshold;
        _brain.MaxContextMessages = _settings.MaxContextMessages;

        // 图片地址过期（QQ 的 rkey 有时效）时让协议端重新签发 —— 那条通道**不随配置变**，
        // 已经在装配点接过一次（CompositionRoot：brain.RefreshImageUrls = …），所以这里不再重复接线。
        // （以前两处都写了一遍，那是搬代码时留下的重影，不是有意的双保险。）
    }

    /// <summary>
    /// 白名单变更后收尾。
    ///
    /// ⚠ 2026-09-21 修语义：白名单只管“要不要回”，**不管“存不存”**。
    /// 以前这里会把不在白名单里的会话从内存里删掉 ✗；随后 <c>Save()</c> 重写 conversations 表时
    /// 那些行就没了 ✗（messages 表的行还在 ✗）—— 后果就是**面板里那个会话直接消失
    /// 或打开是空的**（号主报的“群聊会话不显示”就是这个 ✗）。
    /// 现在只把“不在白名单”的会话列出来，用于清在途的待回复项；
    /// 会话本体与历史**一律保留**（不在白名单的入站在 HandleInbound 那一道就被拦了，不会污染上下文）。
    /// </summary>
    private void PruneNonWhitelisted()
    {
        var removed = _registry.Snapshot().Where(c => !_whitelist.AllowsKey(c.SourceKey)).ToList();

        foreach (var c in removed)
        {
            _reply.ForgetSession(c.SourceKey);
            ForgetSessionState(c.SourceKey);
        }

        if (removed.Count > 0)
        {
            _registry.Save();
        }
    }
}
