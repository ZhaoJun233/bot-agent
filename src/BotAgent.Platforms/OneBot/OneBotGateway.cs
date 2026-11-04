using System.Text.Json;
using BotAgent.Domain.Ports;
using System.Text.Json.Nodes;
using BotAgent.Services.Music;
using BotAgent.Services.Qq;
using BotAgent.Domain.Qq;
using BotAgent.Services.Resilience;
using BotAgent.Services.Platforms;

namespace BotAgent.Services.OneBot;

public enum GatewayState
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>
/// OneBot v11 网关门面：按设置选择传输层，统一分发事件与动作响应。
/// 账号登录方案（NapCat / Lagrange / LLOneBot 等协议端）通过此网关接入，
/// 同时实现统一消息源接口 IQqChatSource。
/// </summary>
public sealed class OneBotGateway : IQqChatSource, IPlatformHistorySource, IQqActions, IDisposable
{
    /// <summary>
    /// 这条路是私域通道（自建协议端 NapCat / OneBot）。
    /// 官方那条是 <c>OfficialBotGateway</c>，两者由 <see cref="ChannelRouter"/> 聚合后交给上层。
    /// </summary>
    public string Channel => Channels.Private;

    private readonly OneBotActionClient _actionsClient;
    private readonly OneBotActions _actions;
    private readonly OneBotQueries _queries;
    private readonly OneBotInboundEvents _inbound;
    private IOneBotTransport? _transport;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _disposed;
    private readonly PlatformOperationLifetime _operations = new();
    // 配置读取入口：指向**当前发布版**（热更新是换引用，见 SettingsBox）——不要改成缓存实例。
    private PlatformOptions _settings => _box.Current;

    private readonly IPlatformSettingsBox _box;
    private readonly Func<PlatformOptions, IOneBotTransport>? _transportFactory;
    private long _selfId;

    /// <summary>
    /// 登录账号 QQ 号提示（headless 版由配置注入）。
    /// 协议端（NapCat）有时不在事件里带 self_id，用它兜底识别 @机器人。
    /// </summary>
    public long SelfIdHint { get; set; }

    public event Action<QqChatMessage>? MessageReceived;
    public event Action<GatewayState>? StateChanged;

    /// <summary>连接状态（IQqChatSource 统一事件）。</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public OneBotGateway(
        IPlatformSettingsBox box,
        ProtocolRiskBackoff? riskBackoff = null,
        Func<PlatformOptions, IOneBotTransport>? transportFactory = null)
    {
        _box = box;
        _transportFactory = transportFactory;
        // 待决响应表与动作面各自成类：网关只留 IQqActions 的入口，动作语义在 OneBotActions。
        _actionsClient = new OneBotActionClient(() => _transport, _operations, () => _disposed);
        _actions = new OneBotActions(riskBackoff, _actionsClient, Log);
        _queries = new OneBotQueries(_actionsClient, () => _selfId, Log);
        // 入站事件面：报文解读与"要不要上报"的判定都在 OneBotInboundEvents，
        // 网关只订阅它、把它的回调转成自己的事件（MessageReceived / Poked / MessageRecalled）。
        _inbound = new OneBotInboundEvents(
            _actionsClient,
            _operations,
            () => _selfId,
            () => SelfIdHint,
            message => MessageReceived?.Invoke(message),
            poke => Poked?.Invoke(poke),
            recall => MessageRecalled?.Invoke(recall),
            Log);
    }

    /// <summary>
    /// 连接信息（协议 / 地址 / Token）相对给定那一份有没有变化，供上层决定是否重建连接。
    /// 只做比较、不再"更新内部引用"：读取一律走 <see cref="SettingsBox" /> 的当前发布版。
    /// </summary>
    public bool UpdateAndMarkIfProtocolChanged(PlatformOptions next)
        => _settings.OneBotProtocol != next.OneBotProtocol
           || _settings.OneBotAddress != next.OneBotAddress
           || _settings.OneBotToken != next.OneBotToken;

    public void Start()
    {
        // Legacy hot reload explicitly rebuilds the configured transport.
        Stop();
        StartAsync().GetAwaiter().GetResult();
    }

    public void Stop()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_transport is not null) return;
            ct.ThrowIfCancellationRequested();
            _operations.Start();
            _cts = new CancellationTokenSource();
            var transport = _transportFactory?.Invoke(_settings) ?? CreateTransport(_settings);
            _transport = transport;
            transport.OnText += _inbound.HandleTransportText;
            transport.OnStateChanged += OnTransportStateChanged;
            try { await transport.StartAsync().ConfigureAwait(false); }
            catch
            {
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
            Log($"已启动 ({_settings.OneBotProtocol} → {_settings.OneBotAddress})");
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        // Once requested, cleanup is not abandoned when an HTTP waiter cancels.
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var transport = _transport;
        _transport = null;
        var lifetime = _cts;
        _cts = null;
        lifetime?.Cancel();
        var ownedRequests = _operations.StopAsync();
        if (transport is not null)
        {
            transport.OnText -= _inbound.HandleTransportText;
            transport.OnStateChanged -= OnTransportStateChanged;
            await transport.StopAsync().ConfigureAwait(false);
            transport.Dispose();
        }
        _actionsClient.CancelAll();
        await ownedRequests.ConfigureAwait(false);
        lifetime?.Dispose();
        SetConnected(false);
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _operations.Dispose();
    }

    /// <summary>收到戳一戳（post_type=notice）。别人互戳也会报上来。</summary>
    public event Action<QqPokeEvent>? Poked;

    /// <summary>收到撤回（post_type=notice 下的 group_recall / friend_recall）。</summary>
    public event Action<QqRecallEvent>? MessageRecalled;

    Task<SendResult> IQqActions.SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct, long? replyToMessageId)
        => SendTextAsync(isGroup, targetId, text, ct, replyToMessageId, directAddress: false);

    // ══════════ 动作面（实现在 OneBotActions；这里只保留 IQqActions 的入口）══════════
    //
    // 为什么网关只做转发：这些方法以前直接拼 JSON 发动作，和连接生命周期、入站事件挤在一个类里。
    // 现在"发什么、失败怎么记"在 OneBotActions，"怎么等回执"在 OneBotActionClient，
    // 网关只声明"这条通道有这些动作"。IQqActions 的签名一个字没改。

    /// <summary>向某个私聊/群聊发送纯文本消息。</summary>
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        => _actions.SendTextAsync(isGroup, targetId, text, ct, replyToMessageId, directAddress);

    public Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => _actions.SendMusicAsync(isGroup, targetId, platform, songId, title, ct);

    public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => _actions.SendVoiceAsync(isGroup, targetId, audioUrl, ct);

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => _actions.SendImageAsync(isGroup, targetId, data, ct, replyToMessageId);

    public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => _actions.SendPokeAsync(isGroup, targetId, userId, ct);

    public Task<bool> SendLikeAsync(long userId, int times, CancellationToken ct = default)
        => _actions.SendLikeAsync(userId, times, ct);

    public Task<bool> SetMessageEmojiLikeAsync(long messageId, string emojiId, CancellationToken ct = default)
        => _actions.SetMessageEmojiLikeAsync(messageId, emojiId, ct);

    public Task<bool> DeleteMessageAsync(long messageId, CancellationToken ct = default)
        => _actions.DeleteMessageAsync(messageId, ct);

    public Task<bool> SetGroupBanAsync(long groupId, long userId, int seconds, CancellationToken ct = default)
        => _actions.SetGroupBanAsync(groupId, userId, seconds, ct);

    public Task<bool> SetGroupKickAsync(long groupId, long userId, bool rejectAdd, CancellationToken ct = default)
        => _actions.SetGroupKickAsync(groupId, userId, rejectAdd, ct);

    public Task<bool> SetGroupCardAsync(long groupId, long userId, string card, CancellationToken ct = default)
        => _actions.SetGroupCardAsync(groupId, userId, card, ct);

    public Task<bool> SetGroupNameAsync(long groupId, string groupName, CancellationToken ct = default)
        => _actions.SetGroupNameAsync(groupId, groupName, ct);

    public Task<bool> SetGroupLeaveAsync(long groupId, bool dismiss, CancellationToken ct = default)
        => _actions.SetGroupLeaveAsync(groupId, dismiss, ct);

    /// <summary>兜底动作调用（协议端握手后的自检，例如获取登录号信息）。</summary>
    public async Task<long?> GetSelfIdAsync(CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_login_info", "{}", ct);
        var id = result?["data"]?["user_id"]?.GetValue<long>();
        if (id is not null)
        {
            _selfId = id.Value;
        }

        return id;
    }

    // ══════════ 查询面（实现在 OneBotQueries；这里只保留对外入口）══════════

    /// <summary>按消息 id 拿回这条消息里所有图片的**当前**地址（OneBot 的 get_msg）。</summary>
    public Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
        => _queries.RefreshImageUrlsAsync(messageId, ct);

    /// <summary>拉取登录账号的"收藏表情"地址（NapCat 扩展动作 fetch_custom_face）。</summary>
    public Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
        => _queries.FetchCustomFacesAsync(count, ct);

    /// <summary>获取群名称（用于会话列表显示）。失败返回 null。</summary>
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => _queries.GetGroupNameAsync(groupId, ct);

    /// <summary>拉群成员资料（身份 + 群头衔）。失败/不支持返回 null。</summary>
    public Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
        => _queries.GetGroupMemberInfoAsync(groupId, userId, ct);

    /// <summary>拉取群历史消息（NapCat 扩展动作 get_group_msg_history）。</summary>
    public Task<List<QqChatMessage>> GetGroupMsgHistoryAsync(long groupId, int count = 20, CancellationToken ct = default)
        => _queries.GetGroupMsgHistoryAsync(groupId, count, ct);

    /// <summary>拉取机器人所在群列表（get_group_list）。</summary>
    public Task<List<(long GroupId, string GroupName)>> GetGroupListAsync(CancellationToken ct = default)
        => _queries.GetGroupListAsync(ct);

    /// <summary>拉取好友列表（get_friend_list）。</summary>
    public Task<List<(long UserId, string Nickname)>> GetFriendListAsync(CancellationToken ct = default)
        => _queries.GetFriendListAsync(ct);

    /// <summary>询问协议端：QQ 账号是否在线。null = 没给明确答复，调用方应保持原状态。</summary>
    public Task<bool?> GetAccountOnlineAsync(CancellationToken ct = default)
        => _queries.GetAccountOnlineAsync(ct);

    /// <summary>按消息 id 查一条消息的正文与发送者（get_msg）。</summary>
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => _queries.GetMessageInfoAsync(messageId, ct);

    // 动作发送与待决响应表在 OneBotActionClient；这里转发给动作面（OneBotActions）与查询面共用。
    private Task<JsonNode?> SendActionAsync(string action, string paramsJson, CancellationToken ct)
        => _actionsClient.SendAsync(action, paramsJson, ct);

    private void OnTransportStateChanged(bool connected)
    {
        if (!_operations.IsActive) return;
        SetConnected(connected);
        if (connected)
        {
            _ = GetSelfIdAsync();
        }
    }

    private static IOneBotTransport CreateTransport(PlatformOptions settings) => settings.OneBotProtocol switch
    {
        "ReverseWebSocket" => new ReverseWsTransport(settings.OneBotAddress, settings.OneBotToken),
        "Http" => new HttpTransport(settings.OneBotAddress, settings.OneBotToken),
        _ => new ForwardWsTransport(settings.OneBotAddress, settings.OneBotToken)
    };

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        StateChanged?.Invoke(connected ? GatewayState.Connected : GatewayState.Disconnected);
        ConnectionChanged?.Invoke(connected);
    }

    private static void Log(string message) =>
        FileLog.Write("OneBot", message);
}
