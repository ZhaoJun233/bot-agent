using System.Collections.Concurrent;
using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

// 本地通道的目标号在**专属号段**（Channels.LocalBase 起）：路由器按数字路由出站，
// 与真实 QQ 号/官方别名三段互不相撞（见 Channels.LocalBase 的注释）。
// 面板入口把配置里的短 id 先过 Channels.LocalTarget 换算成本段的内部号再注入。
namespace BotAgent.Services.Local;

/// <summary>
/// **本地通道**（general-agent-platform-plan.md 批次 F）：第三个 <see cref="IQqChatSource" /> 实现。
///
/// 它存在的**唯一目的**是把"接入层可换"这件事变成可跑的证据：
///   · 入站：面板那张令牌门后的 `POST /api/local/message` → <see cref="InjectAsync" /> → 与 NapCat / 开放平台
///     **走同一条**白名单 → 参与判断 → 回复链 → 工具闸门；
///   · 出站：<see cref="SendTextAsync" /> 不碰网络，只把这条消息记进**内存出箱**（<see cref="Outbox" />）——
///     面板上看得到，harness 也能断言"回复真的发出来了"。
///
/// 三条纪律：
///   · **默认关**：`AppSettings.LocalChannelIds` 为空时这个类根本不会被构造（装配点按名单决定）；
///   · **自带白名单**：只有名单里的本地 id 收（空 = 全拦，与官方的"空 = 全收"故意不同）；
///   · **不越权**：它只是又一条上行 —— 工具、审批、预算、脱敏一概复用，没有任何"本地专用"的例外。
/// </summary>
public sealed class LocalChannelSource : IQqChatSource
{
    /// <summary>出箱最多留多少条（内存台账要有界）。</summary>
    public const int OutboxCapacity = 200;

    private readonly Action<string>? _log;
    private readonly ConcurrentQueue<LocalMessage> _outbox = new();
    private long _nextMessageId = 1;

    public LocalChannelSource(Action<string>? log = null) => _log = log;

    /// <summary>本地通道的会话 key 前缀是 <c>local:</c>（见 <see cref="Channels.Key" />），上下文与另两条路隔离。</summary>
    public string Channel => Channels.Local;

    /// <summary>本地通道没有"连接"这回事（它就是本进程里的一张表）—— 恒为在线。</summary>
    public bool IsConnected => true;

    public event Action<QqChatMessage>? MessageReceived;

    // 这条通道没有"戳一戳 / 撤回 / 连接变化"三种信号（本地消息就是一句话）。
    // 用显式空访问器而不是字段式事件：后者会让编译器每次构建都报 CS0067（"从不使用"），
    // 于是"新增代码不新增警告"这条纪律就被一个**本来就该空着**的东西破坏了。
    public event Action<QqPokeEvent>? Poked { add { } remove { } }

    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }

    public event Action<bool>? ConnectionChanged { add { } remove { } }

    /// <summary>出箱（新的在后）：发出去的东西只记形状与长度，供面板与 harness 读。</summary>
    public IReadOnlyList<LocalMessage> Outbox => _outbox.ToArray();

    /// <summary>没有"目标台账"要注册（本地通道按 id 直发），留着满足接口。</summary>
    public void RegisterTarget(string channel, bool isGroup, long id)
    {
        // 本地通道的目标就是调用方给的 id，没有别名映射要学会 —— 故意什么都不做。
    }

    /// <summary>
    /// 把一条本地消息**注入**成入站消息（与协议端推上来的是同一种东西）。
    /// 面板端点只做参数形状检查；"收不收"由白名单闸门说了算（这里不判，保持与另两条路一致）。
    /// </summary>
    public QqChatMessage Inject(bool isGroup, long id, string senderName, string text, long? messageId = null)
    {
        var msg = new QqChatMessage(
            MessageId: messageId ?? Interlocked.Increment(ref _nextMessageId),
            IsGroup: isGroup,
            UserId: isGroup ? Interlocked.Increment(ref _nextMessageId) : id,
            GroupId: isGroup ? id : 0,
            SenderName: string.IsNullOrWhiteSpace(senderName) ? "本地用户" : senderName.Trim(),
            Text: text ?? string.Empty,
            Time: Clock.Now,
            MentionedSelf: true,          // 本地通道的每一条都是"直接对机器人说的"
            SenderRole: "member",
            Channel: Channels.Local);

        _log?.Invoke($"[本地] 收到一条（{Channels.Key(Channels.Local, isGroup, id)}，{msg.Text.Length} 字）");
        MessageReceived?.Invoke(msg);
        return msg;
    }

    /// <summary>出站：不碰网络，记进出箱（长度与形状，日志也只记长度）。</summary>
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
        long? replyToMessageId = null)
    {
        var key = Channels.Key(Channels.Local, isGroup, targetId);
        var id = Interlocked.Increment(ref _nextMessageId);
        _outbox.Enqueue(new LocalMessage(id, key, text ?? string.Empty, Clock.Now));
        while (_outbox.Count > OutboxCapacity)
        {
            _outbox.TryDequeue(out _);
        }

        _log?.Invoke($"[本地] 回复已记入出箱（{key}，{(text ?? string.Empty).Length} 字）");
        return Task.FromResult(new SendResult(true, id));
    }

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default,
        long? replyToMessageId = null)
        => Task.FromResult(false);   // 本地通道不带图（这一版刻意只做纯文本）

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}

/// <summary>本地通道出箱里的一条（**只有形状与长度**：正文不外传，harness 只看它在不在）。</summary>
public sealed record LocalMessage(long MessageId, string SourceKey, string Text, DateTimeOffset SentAt);
