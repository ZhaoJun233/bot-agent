using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Engine.Runtime;
using BotAgent.Services.OneBot;
using BotAgent.Services.Participation;
using BotAgent.Services.Platforms;
using BotAgent.Services.Permissions;
using BotAgent.Services.Ports;
using BotAgent.Services.Qq;
using System.Text;

namespace BotAgent.Services.Reply;

/// <summary>
/// 入站最前面那几道闸：平台路由是否当前、通道是否登记/连接/启用、白名单、自回显、
/// 跨进程去重、审批命令、括号旁白标注。
///
/// 这些判定只回答一个问题 —— **这条消息要不要继续处理** —— 不产生回复、不碰模型、
/// 不碰会话存储。唯一的对外副作用是节流日志（同一 key 每分钟最多一条）。
/// 从 <see cref="ReplyPipeline"/> 里拆出来（#106 拆分第 3 步），是因为它被 @提及、
/// 白名单、审批、去重四种策略反复改，和"怎么回"没有耦合。
///
/// 顺序不能乱（每条都有 harness 覆盖）：平台当前性 → 通道登记/连接 → 通道启用与聊天开关
/// → 白名单 → 自回显 → （平台通道）信封与去重 → 审批命令 → 括号旁白。
/// </summary>
internal sealed class ReplyInboundGates
{
    private readonly ISettingsBox _box;
    private readonly IQqChatSource _source;
    private readonly IPlatformPolicyResolver _platformPolicies;
    private readonly WhitelistGate _whitelist;
    private readonly ApprovalUseCase _approvals;
    private readonly ParticipationUseCase _participation;
    private readonly IWebhookDedupStore? _inboundDedup;
    private readonly ReplyHooks _hooks;
    private readonly Action<string, string> _logThrottled;

    public ReplyInboundGates(
        ISettingsBox box,
        IQqChatSource source,
        IPlatformPolicyResolver platformPolicies,
        WhitelistGate whitelist,
        ApprovalUseCase approvals,
        ParticipationUseCase participation,
        IWebhookDedupStore? inboundDedup,
        ReplyHooks hooks,
        Action<string, string> logThrottled)
    {
        _box = box;
        _source = source;
        _platformPolicies = platformPolicies;
        _whitelist = whitelist;
        _approvals = approvals;
        _participation = participation;
        _inboundDedup = inboundDedup;
        _hooks = hooks;
        _logThrottled = logThrottled;
    }

    public InboundHead? Inspect(QqChatMessage msg)
    {
        var channel = msg.Channel;
        var source = PlatformSourceExecution.Current;
        if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) return null;
        var platformPolicy = source is null ? _platformPolicies.ResolveForChannel(channel)
            : _platformPolicies.Resolve(source.Context);
        if (!platformPolicy.Registered || !platformPolicy.Connected)
        {
            _logThrottled("unknown-channel:" + channel, $"忽略未注册或未连接的平台通道: {channel}");
            return null;
        }

        var channelEnabled = platformPolicy.Enabled && platformPolicy.ChatEnabled;
        if (!channelEnabled)
        {
            var reason = platformPolicy.Enabled ? "聊天已静音；chat_disabled" : "平台已停用；platform_disabled";
            _logThrottled("chanoff:" + Channels.ChannelOf(msg.Channel), $"忽略（{Channels.Display(msg.Channel)}{reason}）");
            // P1（只观测）：这是“收到了但被服务端拦下” → 对状态机是 Silent 事件（**不改判定**）
            _participation.Observe(
                Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId),
                Services.Participation.ParticipationEvent.Silent);
            return null;
        }

        if (!(source is null ? _whitelist.AllowsMessage(msg) : _whitelist.AllowsMessage(source.Context, msg)))
        {
            // 忙群里这类日志会把日志文件和面板刷爆 → 同一来源每分钟最多一条
            var label = msg.IsGroup ? "群 " + msg.GroupId : "私聊 " + msg.UserId;
            // 把“用哪份名单、那份的状态”一并印出来：
            // 实际踩过——“官方通道永远不回”但日志只有一句“不在白名单”，看不出是名单选错了还是填了真实号。
            var tag = Channels.Tag(msg.Channel);
            var gateNow = _whitelist.Describe();
            var listState = Channels.IsOfficial(msg.Channel)
                ? (msg.IsGroup ? $"官方群名单{gateNow.OfficialGroups}" : $"官方私聊名单{gateNow.OfficialPrivates}")
                : Channels.IsLocal(msg.Channel)
                    ? $"本地名单{gateNow.Local}"
                    : (msg.IsGroup ? "私域群名单" : "私域私聊名单");
            _logThrottled("ignore:" + label, $"忽略（不在白名单）: [{tag}] {label}（{listState}）");
            // P1（只观测）：不在白名单 = 不参与这个话题 → Silent（同样不改判定）
            _participation.Observe(
                Channels.Key(msg.Channel, msg.IsGroup, msg.IsGroup ? msg.GroupId : msg.UserId),
                Services.Participation.ParticipationEvent.Silent);
            return null;
        }

        // 机器人自己发的消息不入库（协议端可能回显）
        if (_hooks.SelfId() != 0 && msg.UserId == _hooks.SelfId())
        {
            return null;
        }

        // 平台通道的共享去重：信封必须来自当前源的当前实例（换个实例就是另一份上下文），
        // 同一条平台消息只处理一次 —— 多进程/重投时靠它兜底（纯私域 NapCat 通道没有信封，跳过）。
        // Shared native dedup follows exact instance policy and whitelist authorization, never precedes it.
        if (source is not null)
        {
            if (msg.Envelope is not { } envelope || envelope.Origin != source.Origin || !source.IsCurrent) return null;
            if (_inboundDedup is null) { _hooks.Log("dedup_store_unavailable"); return null; }
            var reference = envelope.Message.Ref;
            var identity = reference.Conversation;
            var fields = new[] { identity.PlatformId, identity.AccountScope,
                ((int)identity.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
                identity.NativeTargetId, identity.ThreadId ?? string.Empty, reference.NativeMessageId };
            var key = "platform-message:v1:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(fields))));
            try
            {
                if (!_inboundDedup.TryRegister(new[] { key }, EngineClock.Now, TimeSpan.FromMinutes(10))) return null;
            }
            catch (Exception)
            {
                _hooks.Log("dedup_storage_unavailable");
                return null;
            }
        }

        // P3 审批（V3 §9.4，**默认关**）：群主/管理员（或面板点名的人）在**原会话**里回复
        // 「同意 编号」/「拒绝 编号」才算数 —— 身份、会话、有效期、一次性、策略版本全在服务端核。
        // 注意：解析不出“动词 + 编号”这种形状的消息**不拦截**，照旧走普通聊天链路。
        if (_box.Current.EnableApprovals && _approvals.HandleCommand(msg))
        {
            return null;
        }

        // 括号旁白：**标注**（`〔旁白：…〕`）而不是忽略 —— 管理员口径（2026-09-14）：
        // “不要单纯忽略，也要接收，但需要特别注明”。
        // 纯旁白（“（笑）”“（放在地上）来吧”里的括号段）会进聊天记录与上下文，但**不单独触发回复**
        // （旁白不是对谁说的话，不然“（笑）”就会把机器人拽出来接话 —— §20 的原始问题）；
        // 正文 + 旁白混着的照常触发（正文才是那句话）。
        var asideOnly = false;
        if (_box.Current.IgnoreBracketMessages)
        {
            var (isAsideOnly, annotated) = ReplyAnnotations.AnnotateBracketAsides(msg);
            asideOnly = isAsideOnly;

            if (annotated.Length > 0 && annotated != msg.Text)
            {
                msg = msg with { Text = annotated };   // 落库与上下文都用标注后的文本
            }

            if (asideOnly)
            {
                // 同一个人一分钟最多记一条：不然旁白刷屏时日志也跟着刷
                _logThrottled("bracket:" + msg.UserId,
                    $"旁白（只接收、不触发回复）: {msg.SenderName}({msg.UserId}): {TextRules.Shorten(msg.Text, 30)}");
            }
        }

        return new InboundHead(asideOnly, msg);
    }
}

/// <summary>入站最前面那几道闸的产物：标注后的消息 + 是不是纯旁白（null = 这条消息到此为止）。</summary>
public sealed record InboundHead(bool AsideOnly, QqChatMessage Message);
