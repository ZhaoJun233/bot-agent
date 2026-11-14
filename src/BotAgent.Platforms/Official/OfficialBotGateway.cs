using BotAgent.Services.Memory;
using BotAgent.Adapters.Official;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Ports;
using BotAgent.Platforms.Net;
using BotAgent.Services.Platforms;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services.Official;

/// <summary>
/// 官方通道网关（QQ 开放平台 / Bot API v2）——
/// 与私域那条（OneBot + NapCat）**并存**，由 <see cref="ChannelRouter"/> 聚合后交给 BotAgentHost。
///
/// 为什么要有这条路：私域那条是管理员自己的 QQ 号（自建协议端），官方那条是开放平台上申请的机器人
/// （appid + secret，用户是 openid）。两边的会话、上下文、人设互不相干 —— 一起放到面板里看，
/// 但**绝不串台**（靠 <see cref="Channels"/> 的 key 前缀 + <see cref="OfficialIdMap"/> 的别名号）。
///
/// 关键协议事实（2026-09-21 查证）：
///   • 取 token：<c>POST https://bots.qq.com/app/getAppAccessToken</c>，体 <c>{"appId","clientSecret"}</c>，
///     回 <c>{access_token, expires_in(≈7200s)}</c>；调接口带 <c>Authorization: QQBot {token}</c>（**不是 Bearer**）。
///     沙箱与正式取 token 同一个地址。
///   • 网关：<c>GET {api}/gateway/bot</c> 拿 wss 地址（**不硬编码**），identify 用 <c>{op:2,d:{token:"QQBot …",intents:1&lt;&lt;25}}</c>；
///     心跳 op1（d=最近 seq）、重连 op6、op7 服务端要求重连、op9 invalid session、op10 hello、op11 心跳回执。
///   • 事件：<c>GROUP_AT_MESSAGE_CREATE</c>（group_openid / author.member_openid / content / id）、
///     <c>C2C_MESSAGE_CREATE</c>（author.user_openid）。
///   • 发消息：<c>POST /v2/groups/{group_openid}/messages</c> 或 <c>/v2/users/{openid}/messages</c>；
///     带 <c>msg_id</c> = 被动回复（群 5 分钟/5 次、单聊 60 分钟/4 次），不带 = 主动消息（有每日额度）。
///   • 语音/图片：先 <c>POST /v2/{groups|users}/{id}/files</c>（<c>file_type</c>：1 图 / 3 语音）拿
///     <c>file_info</c>，再发 <c>msg_type=7</c> + <c>media:{file_info}</c>。**语音要腾讯 SILK v3**
///     （mp3 直传实测会挂；实测链路上 NapCat 那条反而不用管，它自己转）。
///   • 别忘在开放平台把服务器的出口 IP 加进白名单，否则正式环境连不上。
///
/// 约定：这个类**只管收发**。白名单、限流、上下文、人设全在 BotAgentHost（与私域共用同一套逻辑），
/// 这样两条通道的行为才一致。
/// </summary>
public sealed partial class OfficialBotGateway : IQqChatSource, IStagedReplyChatSource, IDisposable
{
    /// <summary>小组件：群与单聊事件都在这一个位里（GROUP_AND_C2C_EVENT = 1 &lt;&lt; 25）。</summary>
    private const int IntentsGroupAndC2C = 1 << 25;

    /// <summary>音频/图片一次性上传的上限（官方软限制 20MB；超过会降级成“文件”，那就不是语音条了）。</summary>
    private const int MaxUploadBytes = 15 * 1024 * 1024;

    // 配置读取入口：指向**当前发布版**（热更新是换引用，见 SettingsBox）——不要改成缓存实例。
    private PlatformOptions _settings => _box.Current;

    private readonly IPlatformSettingsBox _box;
    private readonly Action<string> _log;
    private readonly Lazy<IOfficialIdentityMap> _ids;
    private readonly string _applicationId;
    private readonly BotAgent.Platforms.Net.IPlatformHttpFetcher _http;

    /// <summary>发送串行化：官方对单聊/群聊有 QPS 限制，机器人这边本来就一句一句发。</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // ---- 会话态（token / session / seq）----
    private readonly object _stateGate = new();
    private string _token = string.Empty;
    private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;
    private string _sessionId = string.Empty;
    private volatile int _lastSeq = -1;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly PlatformOperationLifetime _operations = new();

    public OfficialBotGateway(IPlatformSettingsBox box, BotAgent.Platforms.Net.IPlatformHttpFetcher http, Func<IOfficialIdentityMap> openIdentities,
        Action<string> log, Func<OfficialDeliveryLedger> openDelivery)
    {
        _box = box;
        _log = log;
        _applicationId = box.Current.OfficialAppId;
        _ids = new(openIdentities);
        _delivery = new(openDelivery);
        _http = http;
    }

    /// <summary>这条路是官方通道。</summary>
    public string Channel => Channels.Official;

    public event Action<QqChatMessage>? MessageReceived;

#pragma warning disable CS0067
    public event Action<QqPokeEvent>? Poked;

    public event Action<QqRecallEvent>? MessageRecalled;
#pragma warning restore CS0067

    public event Action<bool>? ConnectionChanged;

    /// <summary>拿到 token 且出现过 READY/RESUMED 才算在线（否则发消息只会得到 304018）。</summary>
    public bool IsConnected { get; private set; }

    public string? LastErrorCode { get; private set; }

    /// <summary>起连接循环（不阻塞；断了自动退避重连）。</summary>
    public void Start()
    {
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
            if (_loop is { IsCompleted: false }) return;
            if (!TryLoadIdentities() || !TryLoadDelivery()) return;
            _operations.Start();
            _cts?.Dispose();
            var lifetime = new CancellationTokenSource();
            _cts = lifetime;
            _loop = Task.Run(() => RunAsync(lifetime.Token));
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _cts?.Cancel();
            await _operations.StopAsync().ConfigureAwait(false);
            if (_loop is { } loop)
            {
                try { await loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _loop = null;
            _cts?.Dispose();
            _cts = null;
            SetConnected(false);
        }
        finally { _lifecycle.Release(); }
    }

    // ═══════════════════ 发送 ═══════════════════

    /// <summary>发文本。被动发送失败保留原结果，不自动改变发送模式。</summary>
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        => SendContentAsync(isGroup, targetId, text, ContentChanges.None, ct, replyToMessageId);

    private async Task<SendResult> SendContentAsync(bool isGroup, long targetId, string text, ContentChanges changes, CancellationToken ct, long? replyToMessageId = null)
    {
        using var destination = MemoryUseScope.EnterLegacyDestination(this, isGroup, targetId);
        using var operation = _operations.TryEnter(ct);
        if (_disposed || operation is null) return RefusedDelivery("delivery_source_invalid");
        // No verified application-domain grant is available to this adapter yet.
        var safeText = StripLinks(text);
        if (!string.Equals(text, safeText, StringComparison.Ordinal)) changes |= ContentChanges.Link;
        var (delivery, reason) = PrepareDelivery(isGroup, targetId, ContentDigest(0, Encoding.UTF8.GetBytes(safeText)), false, replyToMessageId);
        if (delivery is null) return RefusedDelivery(reason);
        delivery = delivery with { Changes = changes };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, delivery.Source.Lifetime);
        try
        {
            var sent = await PostMessageAsync(delivery, new JsonObject { ["msg_type"] = 0, ["content"] = safeText }, lifetime.Token).ConfigureAwait(false);
            return sent.Ok && sent.Delivery is { IsSuccess: true } ? sent with { DeliveredText = safeText } : sent;
        }
        finally { EndDelivery(delivery); }
    }

    /// <summary>
    /// 剥掉正文里的链接（官方平台的 304003：url 未报备）。
    /// 只在官方这条路上用：私域那条件没有这个限制，剥了反而把信息丢了。
    /// </summary>
    private static string StripLinks(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("http", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            @"https?://\S+",
            "（链接已省略：官方通道只能发已报备的域名）",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>兼容 bool 调用方；内部使用完整回执保留降级与未知状态。</summary>
    public async Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => (await SendMusicWithReceiptAsync(isGroup, targetId, platform, songId, title, ct).ConfigureAwait(false)).Ok;

    public Task<SendResult> SendMusicWithReceiptAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
    {
        var site = platform is "163" or "netease" or "" ? "网易云音乐" : platform;
        var name = string.IsNullOrWhiteSpace(title) ? $"（id {songId}）" : $"《{title}》";
        var text = $"🎵 {site}：{name}\n（官方通道发不了可点开的音乐卡片，去 {site} 搜一下就能听～）";
        return SendContentAsync(isGroup, targetId, text, ContentChanges.Music, ct);
    }

    /// <summary>
    /// 发语音：官方平台没有 TTS，语音就是「富媒体 + <c>msg_type=7</c>」。
    /// 音频从 TTS 代理那里**按 silk 取**（官方要求腾讯 SILK v3；mp3 直传会报格式不支持），
    /// 再 base64 内联上传（自建 TTS 在内网，平台拉不到我们的 URL，所以走 file_data）。
    /// </summary>
    public async Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => (await SendVoiceWithReceiptAsync(isGroup, targetId, audioUrl, ct).ConfigureAwait(false)).Ok;

    public async Task<SendResult> SendVoiceWithReceiptAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
    {
        using var destination = MemoryUseScope.EnterLegacyDestination(this, isGroup, targetId);
        using var operation = _operations.TryEnter(ct);
        if (_disposed || operation is null) return RefusedDelivery("delivery_source_invalid");
        if (!TryPrepareAudioUrl(audioUrl, out var url)) return RefusedDelivery("audio_url_invalid");
        var (delivery, reason) = PrepareDelivery(isGroup, targetId, ContentDigest(3, Encoding.UTF8.GetBytes(audioUrl)), true);
        if (delivery is null) return RefusedDelivery(reason);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, delivery.Source.Lifetime);
        try
        {
            var (bytes, downloadReason) = await DownloadAudioAsync(url, lifetime.Token).ConfigureAwait(false);
            if (bytes is null) return RefusedDelivery(downloadReason);
            return await SendMediaAsync(delivery, 3, bytes, lifetime.Token).ConfigureAwait(false);
        }
        finally { EndDelivery(delivery); }
    }

    public async Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => (await SendImageWithReceiptAsync(isGroup, targetId, data, ct, replyToMessageId).ConfigureAwait(false)).Ok;

    public async Task<SendResult> SendImageWithReceiptAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
    {
        using var destination = MemoryUseScope.EnterLegacyDestination(this, isGroup, targetId);
        using var operation = _operations.TryEnter(ct);
        if (_disposed || operation is null) return RefusedDelivery("delivery_source_invalid");
        if (data.Length == 0 || data.Length > MaxUploadBytes) return RefusedDelivery("media_size_invalid");
        // Keep the digest and upload bytes identical even when a caller reuses its mutable buffer.
        var bytes = data.ToArray();
        var (delivery, reason) = PrepareDelivery(isGroup, targetId, ContentDigest(1, bytes), true, replyToMessageId);
        if (delivery is null) return RefusedDelivery(reason);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, delivery.Source.Lifetime);
        try { return await SendMediaAsync(delivery, 1, bytes, lifetime.Token).ConfigureAwait(false); }
        finally { EndDelivery(delivery); }
    }

    /// <summary>
    /// 官方接口不提供群名 —— 返回 null（面板用别名号 + 通道标签展示，别编一个名字出来）。
    /// </summary>
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// 按消息 id 取回原文：官方平台**没有**这个接口（OneBot 的 <c>get_msg</c> 是私域那边独有的扩展）。
    /// 拿不到就要如实返回空 —— 上层会把引用退化成“看不到原文”，而不是编一段出来。
    /// </summary>
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));

    // ═══════════════════ 连接循环 ═══════════════════

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(ct).ConfigureAwait(false);
                backoff = TimeSpan.FromSeconds(3); // 正常跑过一轮（说明连上了），下次别退避
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                LastErrorCode = "connection_timeout";
                Log($"ERROR official_connection reason={LastErrorCode} retry_seconds={backoff.TotalSeconds:F0}");
            }
            catch (Exception)
            {
                if (!_identityRetired) LastErrorCode = "connection_failed";
                Log($"ERROR official_connection reason={LastErrorCode} retry_seconds={backoff.TotalSeconds:F0}");
            }
            finally
            {
                SetConnected(false);
            }

            try
            {
                await Clock.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    /// <summary>一次完整会话：token → 网关地址 → WS → identify/resume → 收消息。</summary>
    private async Task SessionAsync(CancellationToken ct)
    {
        var token = await EnsureTokenAsync(false, ct).ConfigureAwait(false);
        var gatewayUrl = await GetGatewayUrlAsync(token, ct).ConfigureAwait(false);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"QQBot {token}");
        ws.Options.SetRequestHeader("X-Union-Appid", _settings.OfficialAppId);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            await ws.ConnectAsync(new Uri(gatewayUrl), linked.Token).ConfigureAwait(false);

            // 第一条必须是 hello(op10)，里面带心跳间隔
            var hello = await ReceiveJsonAsync(ws, linked.Token).ConfigureAwait(false);
            var intervalMs = AsInt(hello?["d"]?["heartbeat_interval"]) ?? 45000;

            await SendJsonAsync(ws, BuildIdentifyOrResume(token), linked.Token).ConfigureAwait(false);
            Log($"已连上官方网关（心跳 {intervalMs}ms，{(string.IsNullOrEmpty(SessionId) ? "新会话 identify" : "断线 resume")}）");

            var heartbeat = Task.Run(() => HeartbeatLoopAsync(ws, intervalMs, linked.Token), linked.Token);
            try
            {
                await ReceiveLoopAsync(ws, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                linked.Cancel();
                try
                {
                    await heartbeat.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 心跳任务的取消/异常在这条路径上不值得再报一次
                }
            }
        }
    }

    private JsonObject BuildIdentifyOrResume(string token)
    {
        if (!string.IsNullOrEmpty(SessionId))
        {
            return new JsonObject
            {
                ["op"] = 6,
                ["d"] = new JsonObject
                {
                    ["token"] = "QQBot " + token,
                    ["session_id"] = SessionId,
                    ["seq"] = _lastSeq,
                },
            };
        }

        return new JsonObject
        {
            ["op"] = 2,
            ["d"] = new JsonObject
            {
                ["token"] = "QQBot " + token,
                ["intents"] = IntentsGroupAndC2C,
                ["shard"] = new JsonArray(0, 1),
                ["properties"] = new JsonObject
                {
                    ["$os"] = "linux",
                    ["$browser"] = "qqchat-agent",
                    ["$device"] = "qqchat-agent",
                },
            },
        };
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, int intervalMs, CancellationToken ct)
    {
        var period = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs, 5000, 120000));
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await Clock.Delay(period, ct).ConfigureAwait(false);
                await SendJsonAsync(ws, new JsonObject { ["op"] = 1, ["d"] = _lastSeq }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                Log("official_heartbeat reason=transport_failed");
                try
                {
                    ws.Abort(); // 主动 abort：接收循环会立刻退出，交给外层退避重连
                }
                catch (Exception)
                {
                    // 已经断了就算了
                }

                return;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var frame = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false);
            if (frame is null)
            {
                Log("官方网关连接已关闭");
                return;
            }

            var op = AsInt(frame["op"]) ?? -1;
            var seq = AsInt(frame["s"]);
            if (seq is not null)
            {
                _lastSeq = seq.Value;
            }

            switch (op)
            {
                case 0: // dispatch
                    await HandleDispatchAsync(Text(frame["t"]), frame["d"] as JsonObject, frame["d"], ct).ConfigureAwait(false);
                    break;
                case 7: // 服务端要求重连（带 session 可以 resume）
                    Log("官方网关要求重连");
                    return;
                case 9: // invalid session：d=false 必须清 session 重新 identify
                    if (frame["d"]?.GetValue<bool>() == false)
                    {
                        Log("session 失效（不能再 resume）→ 清掉重新 identify");
                        SessionId = string.Empty;
                    }

                    return;
                case 10:
                    // hello 在 SessionAsync 里已经处理过；这里是重连后第二次收到，忽略即可
                    break;
                case 11: // 心跳回执
                case 1: // 服务端主动要一次心跳
                    if (op == 1)
                    {
                        await SendJsonAsync(ws, new JsonObject { ["op"] = 1, ["d"] = _lastSeq }, ct).ConfigureAwait(false);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private void HandleDispatch(string? type, JsonObject? d, JsonNode? raw)
    {
        if (!TryLoadIdentities()) return;
        try { HandleIdentityDispatch(type, d, raw); }
        catch (Exception error) when (IsIdentityFailure(error)) { RejectIdentity(error); }
    }

    private void HandleIdentityDispatch(string? type, JsonObject? d, JsonNode? raw)
    {
        if (string.IsNullOrEmpty(type) || d is null)
        {
            return;
        }

        // 探针（2026-09-21）：官方通道“不回复、日志里也啥都没有”时，先要能分开两种情况：
        //   ① 平台根本没推 → 这行不出现；
        //   ② 推来了但被丢掉/解析炸了 → 这行出现，后面却没有“入站”行。
        Log(type is "READY" or "RESUMED" or "GROUP_AT_MESSAGE_CREATE" or "GROUP_MESSAGE_CREATE"
            or "C2C_MESSAGE_CREATE" or "GROUP_ADD_ROBOT" or "GROUP_DEL_ROBOT" or "GROUP_MEMBER_REMOVE"
            or "FRIEND_ADD" or "FRIEND_DEL" or "GROUP_MSG_REJECT" or "GROUP_MSG_RECEIVE" or "C2C_MSG_REJECT" or "C2C_MSG_RECEIVE"
            ? "官方事件：" + type : "official_event kind=other");

        switch (type)
        {
            case "READY":
                InvalidateGroupSelf();
                InvalidateMemberRoles();
                InvalidateReferences();
                SessionId = Text(d["session_id"]) ?? string.Empty;
                Log($"official_ready identities={_ids.Value.Count}");
                SetConnected(true);
                return;

            case "RESUMED":
                Log("官方网关会话已恢复");
                SetConnected(true);
                return;

            case "GROUP_AT_MESSAGE_CREATE":
                RaiseGroupMessage(d);
                return;

            case "GROUP_MESSAGE_CREATE":
                // 全量模式：群主给机器人开了“获取群内全部消息”之后，群里**每一条**都会推过来
                // （不只是 @ 它的那些）。所以必须自己判断“这句是不是冲它说的”，
                // 否则会把别人的聊天全当成点名，见 RaiseGroupMessage 的 fullMode 分支。
                RaiseGroupMessage(d, fullMode: true);
                return;

            case "C2C_MESSAGE_CREATE":
                RaiseC2CMessage(d);
                return;

            case "GROUP_ADD_ROBOT":
                RelationshipChanged(true, false, d);
                return;

            case "GROUP_DEL_ROBOT":
                RelationshipChanged(true, true, d);
                return;

            case "FRIEND_ADD":
                RelationshipChanged(false, false, d);
                return;

            case "FRIEND_DEL":
                RelationshipChanged(false, true, d);
                return;

            case "GROUP_MSG_REJECT":
            case "GROUP_MSG_RECEIVE":
                NotificationChanged(true, d);
                return;

            case "C2C_MSG_REJECT":
            case "C2C_MSG_RECEIVE":
                NotificationChanged(false, d);
                return;

            case "GROUP_MEMBER_REMOVE":
                RemoveMemberRole(d);
                return;

            default:
                // 其它事件（主动消息被拒/审核…）先只记一条，不影响对话
                if (type.Contains("REJECT", StringComparison.Ordinal))
                {
                    Log("official_event reason=unhandled_rejection");
                }

                return;
        }
    }

    private void RaiseGroupMessage(JsonObject d, bool fullMode = false)
    {
        var memberOpenId = ReadSenderId(d, "member_openid");
        if (memberOpenId is null)
        {
            Log("官方入站拒绝 reason=identity_missing kind=group");
            return;
        }

        var rawId = ReadIdentityId(d["id"]);
        var groupOpenId = ReadIdentityId(d["group_openid"]);
        var self = CurrentGroupSelf(groupOpenId);
        if (RejectSelfSender(self, memberOpenId)) return;
        var message = MessageIdentity(true, groupOpenId, rawId, OfficialMessageDirection.Inbound, OfficialReferenceFields.ReadMessageIndex(d));
        var subject = new OfficialIdentityKey(new(_applicationId, OfficialIdentityKind.GroupMember, message.Scope.Conversation), memberOpenId);
        var role = ObserveMemberRole(d, subject, fullMode);
        var eventTime = RoleEventTime(d["timestamp"]);
        if (!AcceptInboundDelivery(message, eventTime)) return;
        var groupAlias = _ids.Value.AliasFor(TargetIdentity(true, groupOpenId));
        var userAlias = _ids.Value.AliasFor(subject);
        var messageAlias = _ids.Value.AliasFor(message);
        var quote = ObserveReferences(message, messageAlias, d);
        LastErrorCode = null;
        // 入站探针：走到这里说明平台推的、解析的、判重的都过了。
        // 之后若还是没有回复，问题就在上层（白名单/总开关/限流/模型），日志会有对应行。
        Log($"官方入站：群 {groupAlias}，用户 {userAlias}"
            + (fullMode ? $"（全量模式，@我={MentionsSelf(d, self)}）" : string.Empty));

        // 是不是“冲它说的”：
        //   @ 事件 → 平台只会推 @ 它的，必然是 true；
        //   全量模式 → 标识必须匹配本群已验证的机器人身份。
        var mentioned = !fullMode || MentionsSelf(d, self);
        var content = Text(d["content"]) ?? string.Empty;
        if (fullMode && mentioned)
        {
            content = StripLeadingSelfMention(content, self);
        }
        var media = ReadInboundContent(d, content);

        MessageReceived?.Invoke(new QqChatMessage(
            MessageId: messageAlias,
            IsGroup: true,
            UserId: userAlias,
            GroupId: groupAlias,
            SenderName: DisplayName(d, userAlias),
            Text: media.Text,
            Time: eventTime!.Value,
            MentionedSelf: mentioned,
            ImageUrls: media.Images,
            Channel: Channels.Official, ReplyToMessageId: quote.Alias, ReplyToPreviewText: quote.Preview)
            { IsReliableIdentity = true, OfficialRole = role, AllowsCommands = media.AllowsCommands, VoiceAttachments = media.Voices });
    }

    // 身份只能来自完整的字符串字段；拒绝发生在去重、别名分配和上层副作用之前。
    private static string? ReadSenderId(JsonObject data, string field)
    {
        if ((data["author"] as JsonObject)?[field] is not JsonValue node
            || !node.TryGetValue<string>(out var id) || string.IsNullOrEmpty(id)
            || id.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            return null;
        }
        return id;
    }

    private void RaiseC2CMessage(JsonObject d)
    {
        var userOpenId = ReadSenderId(d, "user_openid");
        if (userOpenId is null)
        {
            Log("官方入站拒绝 reason=identity_missing kind=c2c");
            return;
        }

        var rawId = ReadIdentityId(d["id"]);
        var message = MessageIdentity(false, userOpenId, rawId, OfficialMessageDirection.Inbound, OfficialReferenceFields.ReadMessageIndex(d));
        var eventTime = RoleEventTime(d["timestamp"]);
        if (!AcceptInboundDelivery(message, eventTime)) return;
        var userAlias = _ids.Value.AliasFor(TargetIdentity(false, userOpenId));
        var messageAlias = _ids.Value.AliasFor(message);
        var quote = ObserveReferences(message, messageAlias, d);
        LastErrorCode = null;
        Log($"官方入站：私聊 {userAlias}");
        var media = ReadInboundContent(d, Text(d["content"]) ?? string.Empty);

        MessageReceived?.Invoke(new QqChatMessage(
            MessageId: messageAlias,
            IsGroup: false,
            UserId: userAlias,
            GroupId: 0,
            SenderName: DisplayName(d, userAlias),
            Text: media.Text,
            Time: eventTime!.Value,
            ImageUrls: media.Images,
            MentionedSelf: true, // 单聊本来就是一对一
            Channel: Channels.Official, ReplyToMessageId: quote.Alias, ReplyToPreviewText: quote.Preview)
            { IsReliableIdentity = true, AllowsCommands = media.AllowsCommands, VoiceAttachments = media.Voices });
    }

    private void SetConnected(bool connected)
    {
        if (!connected) { InvalidateGroupSelf(); InvalidateMemberRoles(); InvalidateReferences(); }
        if (connected && LastErrorCode is "connection_timeout" or "connection_failed") LastErrorCode = null;
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    // ═══════════════════ REST ═══════════════════

    private string ApiBase => !string.IsNullOrWhiteSpace(_settings.OfficialApiBase)
        ? _settings.OfficialApiBase.TrimEnd('/')
        : _settings.OfficialSandbox ? "https://sandbox.api.sgroup.qq.com" : "https://api.bot.qq.com";

    private string TokenUrl => !string.IsNullOrWhiteSpace(_settings.OfficialTokenUrl)
        ? _settings.OfficialTokenUrl
        : "https://bots.qq.com/app/getAppAccessToken";

    private async Task<string> EnsureTokenAsync(bool force, CancellationToken ct)
    {
        if (!ApplicationIsCurrent()) throw new InvalidOperationException("identity_source_changed");
        lock (_stateGate)
        {
            if (!force && !string.IsNullOrEmpty(_token) && Clock.Now < _tokenExpires)
            {
                return _token;
            }
        }

        var body = new JsonObject
        {
            ["appId"] = _settings.OfficialAppId,
            ["clientSecret"] = _settings.OfficialAppSecret,
        };

        using var resp = await _http.PostAsync(TokenUrl, JsonBody(body), ct).ConfigureAwait(false);
        var (token, expires) = await ReadTokenAsync(resp, ct).ConfigureAwait(false);
        lock (_stateGate)
        {
            _token = token;
            // 提前 5 分钟刷新：避免刚好卡在过期那一刻发消息失败
            _tokenExpires = Clock.Now + TimeSpan.FromSeconds(Math.Max(60, expires - 300));
        }

        Log($"official_token acquired=1 expires_seconds={expires}");
        return token;
    }

    private async Task<string> GetGatewayUrlAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/gateway/bot");
        Auth(req, token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"official_gateway_http_rejected status={(int)resp.StatusCode}");
        }

        var url = Text(JsonNode.Parse(text)?["url"]);
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException("official_gateway_url_unavailable");
        }

        return url;
    }

    private async Task<SendResult> PostMessageAsync(DeliveryContext delivery, JsonObject body, CancellationToken ct)
    {
        body["msg_id"] = delivery.Trigger.NativeId;
        body["msg_seq"] = delivery.Sequence;
        if (delivery.ReferenceIndex is not null) body["message_reference"] = new JsonObject { ["message_id"] = delivery.ReferenceIndex };
        var url = $"{ApiBase}/v2/{(delivery.IsGroup ? "groups" : "users")}/{Uri.EscapeDataString(delivery.Target)}/messages";
        var result = await SendApiAsync(url, () => JsonBody(body), delivery.IsGroup ? OfficialApiEndpoint.GroupMessage : OfficialApiEndpoint.DirectMessage,
            delivery, OfficialDeliveryPhase.Message, ct, isChatCommit: true).ConfigureAwait(false);
        var alias = long.TryParse(result.Delivery.MessageId, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return new SendResult(result.Delivery.IsSuccess, alias) { Delivery = result.Delivery };
    }

    private async Task<SendResult> SendMediaAsync(DeliveryContext delivery, int fileType, byte[] data, CancellationToken ct)
    {
        if (data.Length == 0 || data.Length > MaxUploadBytes) return RefusedDelivery("media_size_invalid");
        var url = $"{ApiBase}/v2/{(delivery.IsGroup ? "groups" : "users")}/{Uri.EscapeDataString(delivery.Target)}/files";
        var result = await SendApiAsync(url, () => new OfficialMediaContent(fileType, data),
            delivery.IsGroup ? OfficialApiEndpoint.GroupUpload : OfficialApiEndpoint.DirectUpload,
            delivery, OfficialDeliveryPhase.Upload, ct).ConfigureAwait(false);
        if (!result.Delivery.IsSuccess) return new(false) { Delivery = result.Delivery };
        Log($"official_upload file_type={fileType} bytes={data.Length} ttl={result.Ttl?.ToString() ?? "unknown"}");
        return await PostMessageAsync(delivery, new JsonObject
        {
            ["msg_type"] = 7, ["media"] = new JsonObject { ["file_info"] = result.FileInfo },
        }, ct).ConfigureAwait(false);
    }

    private static void Auth(HttpRequestMessage req, string token)
    {
        // 官方要的是 "QQBot {token}"，不是 Bearer（写成 Bearer 会被 1127 系列拒掉）
        req.Headers.TryAddWithoutValidation("Authorization", "QQBot " + token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static StringContent JsonBody(JsonObject body)
        => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // ═══════════════════ WS 收发 ═══════════════════

    private static Task SendJsonAsync(ClientWebSocket ws, JsonObject payload, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(payload.ToJsonString()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonNode?> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null; // 连接断了：交给外层重连
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 4 * 1024 * 1024)
            {
                return null; // 畸形帧：别把内存吃光
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ═══════════════════ 小工具 ═══════════════════

    private string SessionId
    {
        get => _sessionId;
        set { lock (_stateGate) { _sessionId = value; } }
    }

    private static string? Text(JsonNode? node)
    {
        // 协议端字段类型不稳定（数字/字符串都出现过）——统一走这里，别用 GetValue<string>() 硬取
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
                System.Text.Json.JsonValueKind.Number => node.ToJsonString(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? AsInt(JsonNode? node)
    {
        var text = Text(node);
        return int.TryParse(text, out var value) ? value : null;
    }

    private void Log(string message) => _log(message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _operations.Dispose();
        _cts?.Dispose();
        _sendGate.Dispose();
        if (_delivery.IsValueCreated) _delivery.Value.Dispose();
        // 出网客户端由装配点持有并统一释放（见 HttpFetcher），这里只收自己的东西
    }
}
