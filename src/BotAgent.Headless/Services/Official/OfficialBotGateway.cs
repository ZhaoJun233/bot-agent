using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Official;

/// <summary>
/// 官方通道网关（QQ 开放平台 / Bot API v2）——
/// 与私域那条（OneBot + NapCat）**并存**，由 <see cref="ChannelRouter"/> 聚合后交给 BotAgentHost。
///
/// 为什么要有这条路：私域那条是号主自己的 QQ 号（自建协议端），官方那条是开放平台上申请的机器人
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
public sealed class OfficialBotGateway : IQqChatSource, IDisposable
{
    /// <summary>小组件：群与单聊事件都在这一个位里（GROUP_AND_C2C_EVENT = 1 &lt;&lt; 25）。</summary>
    private const int IntentsGroupAndC2C = 1 << 25;

    /// <summary>
    /// 被动回复的有效期。官方文档两处口径不一（群 5 分钟 / 单聊 60 分钟 vs 一律 5 分钟），
    /// 这里取**保守值** —— 超窗口就发主动消息，宁可少一次引用也别整条发不出去。
    /// </summary>
    private static readonly TimeSpan PassiveWindow = TimeSpan.FromMinutes(5);

    /// <summary>音频/图片一次性上传的上限（官方软限制 20MB；超过会降级成“文件”，那就不是语音条了）。</summary>
    private const int MaxUploadBytes = 15 * 1024 * 1024;

    // 配置读取入口：指向**当前发布版**（热更新是换引用，见 SettingsBox）——不要改成缓存实例。
    private AppSettings _settings => _box.Current;

    private readonly SettingsBox _box;
    private readonly Action<string> _log;
    private readonly IOfficialIdMap _ids;
    private readonly IHttpFetcher _http;

    /// <summary>发送串行化：官方对单聊/群聊有 QPS 限制，机器人这边本来就一句一句发。</summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // ---- 会话态（token / session / seq）----
    private readonly object _stateGate = new();
    private string _token = string.Empty;
    private DateTimeOffset _tokenExpires = DateTimeOffset.MinValue;
    private string _sessionId = string.Empty;
    private volatile int _lastSeq = -1;

    // ---- 去重：官方可能把同一条消息重复推过来 ----
    private const int SeenCapacity = 200;
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _seenGate = new();

    /// <summary>会话（group_openid / user_openid）→ 最近一条入站消息的**原始** id（被动回复要原样回填）。</summary>
    private readonly ConcurrentDictionary<string, (string RawId, DateTimeOffset At)> _lastInbound = new(StringComparer.Ordinal);

    /// <summary>原始 msg_id → 已经用掉的 msg_seq（同一条消息多次回复必须递增，否则会被拒）。</summary>
    private readonly ConcurrentDictionary<string, int> _msgSeq = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public OfficialBotGateway(SettingsBox box, IHttpFetcher http, IOfficialIdMap ids, Action<string> log)
    {
        _box = box;
        _log = log;
        _ids = ids;
        _http = http;
    }

    /// <summary>这条路是官方通道。</summary>
    public string Channel => Channels.Official;

    public event Action<QqChatMessage>? MessageReceived;

    public event Action<QqPokeEvent>? Poked;

    public event Action<QqRecallEvent>? MessageRecalled;

    public event Action<bool>? ConnectionChanged;

    /// <summary>拿到 token 且出现过 READY/RESUMED 才算在线（否则发消息只会得到 304018）。</summary>
    public bool IsConnected { get; private set; }

    /// <summary>别名台账（面板/命令里把 openid 与数字号对上时用）。</summary>
    public IOfficialIdMap Ids => _ids;

    /// <summary>起连接循环（不阻塞；断了自动退避重连）。</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // 退出路径：别因为收尾失败把整个进程拖死
        }

        _ids.Flush();
    }

    // ═══════════════════ 发送 ═══════════════════

    /// <summary>发文本。优先当**被动回复**（带上刚收到的 msg_id，能引用上下文），失败就退化成主动消息。</summary>
    public async Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null)
    {
        var openId = _ids.OriginalOf(targetId);
        if (string.IsNullOrEmpty(openId))
        {
            Log($"发送失败：{targetId} 在别名表里没有对应 openid（是不是重启后表被清了？）");
            return new SendResult(false);
        }

        var passive = TakePassiveRef(openId);
        // ⚠ 官方平台会拒绝**未报备的链接**（回 304003 URL_NOT_ALLOWED）——而模型很爱在回复里塞链接，
        // 结果是整条消息发不出去 ✗。这里把 http(s) 链接换成占位文本，至少把话说出去；
        // 链接本身在私域那条路照旧正常（那边没这个限制）。
        var safeText = StripLinks(text);
        var sent = await PostMessageAsync(isGroup, openId, body =>
        {
            body["msg_type"] = 0;
            body["content"] = safeText;
        }, passive, ct).ConfigureAwait(false);

        if (!sent.Ok && passive is not null)
        {
            // 被动窗口过了 / 次数用尽 / 引用被拒 —— 退回主动消息，至少让话送到
            Log("被动回复失败（超过窗口或次数用尽）→ 改用主动消息再发一次");
            sent = await PostMessageAsync(isGroup, openId, body =>
            {
                body["msg_type"] = 0;
                body["content"] = safeText;
            }, null, ct).ConfigureAwait(false);
        }

        return sent;
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

    /// <summary>
    /// <summary>
    /// 「听音乐/点歌」在官方通道的降级实现。
    ///
    /// 官方平台**没有** OneBot 那种 music 卡片（可点开播放的）✗，而且链接必须**报备**（没报备报 304003 ✗，
    /// 我们发送前还会主动剥链接）——所以这里既不能发卡片、也不能发链接。
    /// 能做且真正有用的是：发一条**带歌名的文字**（让人能去网易云搜到它），
    /// 而不是丢一个裸 id 或者什么都没发。返回 true 表示“已用降级形式发出”，
    /// 上层就不会再去发那个（在官方通道会被剥掉的）链接了。
    /// </summary>
    public async Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
    {
        var site = platform is "163" or "netease" or "" ? "网易云音乐" : platform;
        var name = string.IsNullOrWhiteSpace(title) ? $"（id {songId}）" : $"《{title}》";
        var text = $"🎵 {site}：{name}\n（官方通道发不了可点开的音乐卡片，去 {site} 搜一下就能听～）";
        var sent = await SendTextAsync(isGroup, targetId, text, ct).ConfigureAwait(false);
        Log(sent.Ok
            ? $"官方通道音乐已降级为文字：{name}（官方无卡片、链接需报备）"
            : $"官方通道音乐降级文字也发送失败（messageId={sent.MessageId}）");
        return sent.Ok;
    }

    /// 发语音：官方平台没有 TTS，语音就是「富媒体 + <c>msg_type=7</c>」。
    /// 音频从 TTS 代理那里**按 silk 取**（官方要求腾讯 SILK v3；mp3 直传会报格式不支持），
    /// 再 base64 内联上传（自建 TTS 在内网，平台拉不到我们的 URL，所以走 file_data）。
    /// </summary>
    public async Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
    {
        var openId = _ids.OriginalOf(targetId);
        if (string.IsNullOrEmpty(openId))
        {
            Log($"发语音失败：{targetId} 没有对应 openid");
            return false;
        }

        var url = EnsureFormat(audioUrl, "silk");
        var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            Log($"语音取不到音频（{url}）→ 上层会退化成发文字");
            return false;
        }

        var (fileInfo, error) = await UploadAsync(isGroup, openId, 3, bytes, "voice.silk", ct).ConfigureAwait(false);
        if (fileInfo is null)
        {
            Log($"语音上传失败：{error}（官方对语音只认腾讯 SILK v3；若是格式类报错，检查 TTS 侧 silk 编码器）");
            return false;
        }

        return await SendMediaAsync(isGroup, openId, fileInfo, ct).ConfigureAwait(false);
    }

    /// <summary>发图片：同样是富媒体（file_type=1）+ <c>msg_type=7</c>。</summary>
    public async Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
    {
        var openId = _ids.OriginalOf(targetId);
        if (string.IsNullOrEmpty(openId) || data.Length == 0)
        {
            return false;
        }

        var (fileInfo, error) = await UploadAsync(isGroup, openId, 1, data, "image.png", ct).ConfigureAwait(false);
        if (fileInfo is null)
        {
            Log($"图片上传失败：{error}");
            return false;
        }

        return await SendMediaAsync(isGroup, openId, fileInfo, ct).ConfigureAwait(false);
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"连接异常（{backoff.TotalSeconds:F0}s 后重连）：{ex.Message}");
            }

            SetConnected(false);

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
            catch (Exception ex)
            {
                Log($"心跳发送失败（{ex.Message}）→ 让接收循环去重连");
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
                    HandleDispatch(Text(frame["t"]), frame["d"] as JsonObject, frame["d"]);
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
        if (string.IsNullOrEmpty(type) || d is null)
        {
            return;
        }

        // 探针（2026-09-21）：官方通道“不回复、日志里也啥都没有”时，先要能分开两种情况：
        //   ① 平台根本没推 → 这行不出现；
        //   ② 推来了但被丢掉/解析炸了 → 这行出现，后面却没有“入站”行。
        Log($"官方事件：{type}");

        switch (type)
        {
            case "READY":
                SessionId = Text(d["session_id"]) ?? string.Empty;
                Log($"官方机器人已上线（session {Mask(SessionId)}，别名表里已有 {_ids.Count} 个标识）");
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
                // 被拉进群：记一句人话（之后这个群的消息才会推过来）
                Log($"✅ 机器人被加进官方群 {_ids.AliasFor(Text(d["group_openid"]) ?? string.Empty)}"
                    + (string.IsNullOrWhiteSpace(Text(d["op_member_openid"])) ? string.Empty : "（群主/管理员操作）"));
                return;

            case "GROUP_DEL_ROBOT":
                // ⚠ 被移出群：**平台从此不再推送这个群的任何事件**（@ 它也不会推、日志里一行都不会有）。
                // 2026-09-21 线上卡了很久的“艾特了日志根本不显示”，根因就是这条事件：
                // 18:06 机器人被移出群，之后所有 READY 之外再无任何群消息 ✗。所以这句要显眼。
                Log($"⚠ 机器人被移出官方群 {_ids.AliasFor(Text(d["group_openid"]) ?? string.Empty)} —— "
                    + "此后该群的任何消息都不会推过来（@ 也不推、日志里不会有行）。要恢复：把机器人重新加进群，"
                    + "并在群设置里重新允许它获取群内消息。");
                return;

            default:
                // 其它事件（主动消息被拒/审核…）先只记一条，不影响对话
                if (type.Contains("REJECT", StringComparison.Ordinal))
                {
                    Log($"官方事件 {type}：{Snippet(raw?.ToJsonString() ?? string.Empty, 160)}");
                }

                return;
        }
    }

    private void RaiseGroupMessage(JsonObject d, bool fullMode = false)
    {
        var rawId = Text(d["id"]);
        var groupOpenId = Text(d["group_openid"]);
        // 判重只问一次（IsDuplicate 会“记下来”，问两次会把正常消息误判成重复）
        var duplicate = rawId is not null && IsDuplicate(rawId);
        if (string.IsNullOrEmpty(rawId) || string.IsNullOrEmpty(groupOpenId) || duplicate)
        {
            Log($"官方群消息丢弃：id {(rawId is null ? "缺失" : "有")}、group_openid {(groupOpenId is null ? "缺失" : "有")}、重复 {duplicate}");
            return;
        }

        var memberOpenId = Text((d["author"] as JsonObject)?["member_openid"]);
        var groupAlias = _ids.AliasFor(groupOpenId);
        var userAlias = _ids.AliasFor(string.IsNullOrEmpty(memberOpenId) ? groupOpenId : memberOpenId);

        RememberInbound(groupOpenId, rawId);
        // 入站探针：走到这里说明平台推的、解析的、判重的都过了。
        // 之后若还是没有回复，问题就在上层（白名单/总开关/限流/模型），日志会有对应行。
        Log($"官方入站：群 {_ids.AliasFor(groupOpenId)}，用户 {_ids.AliasFor(string.IsNullOrEmpty(memberOpenId) ? groupOpenId : memberOpenId)}"
            + (fullMode ? $"（全量模式，@我={(!fullMode || MentionsSelf(d))}）" : string.Empty));

        // 是不是“冲它说的”：
        //   @ 事件 → 平台只会推 @ 它的，必然是 true；
        //   全量模式 → 得自己看 mentions[].is_you（别人聊天也会来，绝不能一律 true）。
        var mentioned = !fullMode || MentionsSelf(d);
        var content = Text(d["content"]) ?? string.Empty;
        if (fullMode && mentioned)
        {
            content = StripLeadingMention(content);
        }

        MessageReceived?.Invoke(new QqChatMessage(
            MessageId: _ids.AliasFor(rawId),
            IsGroup: true,
            UserId: userAlias,
            GroupId: groupAlias,
            SenderName: DisplayName(memberOpenId),
            Text: content,
            Time: ParseTime(d["timestamp"]),
            MentionedSelf: mentioned,
            ImageUrls: Attachments(d),
            Channel: Channels.Official));
    }

    /// <summary>
    /// 事件里有没有“提到机器人自己”（<c>mentions[].is_you == true</c>）。
    /// 全量群消息靠它区分“点名找我”和“别人自己聊天”—— 两者的后续处理完全不一样。
    /// </summary>
    private static bool MentionsSelf(JsonObject d)
    {
        if (d["mentions"] is not JsonArray arr)
        {
            return false;
        }

        foreach (var item in arr)
        {
            if (item is JsonObject o && o["is_you"]?.GetValue<bool>() == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 剥掉正文开头的“@机器人”。全量模式下平台不会替我们剥（@ 事件才会），
    /// 所以这里自己来：既处理 <c>&lt;@!id&gt;</c> 这种标记，也处理纯文本的 <c>@名字 </c>。
    /// </summary>
    private static string StripLeadingMention(string text)
    {
        var t = (text ?? string.Empty).TrimStart();
        while (t.StartsWith("<@", StringComparison.Ordinal))
        {
            var end = t.IndexOf('>');
            if (end < 0)
            {
                break;
            }

            t = t[(end + 1)..].TrimStart();
        }

        if (t.StartsWith('@'))
        {
            var space = t.IndexOf(' ');
            if (space > 0 && space <= 40)
            {
                t = t[(space + 1)..].TrimStart();
            }
        }

        return t;
    }

    /// <summary>
    /// 取事件里的图片附件地址（<c>d.attachments[]</c>，<c>content_type</c> 以 image/ 开头的那些）。
    /// 为什么要它：官方平台发图片时正文是空的/只有一句“发了一张图片”，真正的东西在 attachments 里；
    /// 不解析的话机器人就是“看不见图”（私域那条早就支持了，这里要对齐）。
    /// </summary>
    private static IReadOnlyList<string>? Attachments(JsonObject d)
    {
        if (d["attachments"] is not JsonArray arr || arr.Count == 0)
        {
            return null;
        }

        var urls = new List<string>();
        foreach (var node in arr)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var type = Text(item["content_type"]) ?? string.Empty;
            var url = Text(item["url"]);
            if (url is not null && type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(url);
            }
        }

        return urls.Count > 0 ? urls : null;
    }

    private void RaiseC2CMessage(JsonObject d)
    {
        var rawId = Text(d["id"]);
        var userOpenId = Text((d["author"] as JsonObject)?["user_openid"]);
        if (string.IsNullOrEmpty(rawId) || string.IsNullOrEmpty(userOpenId) || IsDuplicate(rawId))
        {
            return;
        }

        RememberInbound(userOpenId, rawId);
        Log($"官方入站：私聊 {_ids.AliasFor(string.IsNullOrEmpty(userOpenId) ? "?" : userOpenId)}");

        MessageReceived?.Invoke(new QqChatMessage(
            MessageId: _ids.AliasFor(rawId),
            IsGroup: false,
            UserId: _ids.AliasFor(userOpenId),
            GroupId: 0,
            SenderName: DisplayName(userOpenId),
            Text: Text(d["content"]) ?? string.Empty,
            Time: ParseTime(d["timestamp"]),
            ImageUrls: Attachments(d),
            MentionedSelf: true, // 单聊本来就是一对一
            Channel: Channels.Official));
    }

    /// <summary>
    /// 显示名：官方只给 openid（没有昵称），所以取它末尾四位当稳定后缀 ——
    /// **不编造**昵称，也不把完整 openid 写进会话名（面板脱敏之外再加一道）。
    /// </summary>
    private static string DisplayName(string? openId)
        => string.IsNullOrEmpty(openId) || openId.Length < 4 ? "官方用户" : "官方用户" + openId[^4..];

    private void SetConnected(bool connected)
    {
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
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"取 access_token 失败 HTTP {(int)resp.StatusCode}：{Snippet(text, 200)}");
        }

        var json = JsonNode.Parse(text);
        var token = Text(json?["access_token"]);
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException($"取 access_token 失败：{Snippet(text, 200)}");
        }

        var expires = AsInt(json?["expires_in"]) ?? 7200;
        lock (_stateGate)
        {
            _token = token;
            // 提前 5 分钟刷新：避免刚好卡在过期那一刻发消息失败
            _tokenExpires = Clock.Now + TimeSpan.FromSeconds(Math.Max(60, expires - 300));
        }

        Log($"access_token 已获取（约 {expires} 秒有效期，token {Mask(token)}）");
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
            throw new InvalidOperationException($"取网关地址失败 HTTP {(int)resp.StatusCode}：{Snippet(text, 200)}");
        }

        var url = Text(JsonNode.Parse(text)?["url"]);
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException($"网关地址为空：{Snippet(text, 200)}");
        }

        return url;
    }

    /// <summary>发消息（<c>msg_type</c> 由 <paramref name="fill"/> 填）。passive 非 null 时带 msg_id + msg_seq。</summary>
    private async Task<SendResult> PostMessageAsync(
        bool isGroup,
        string openId,
        Action<JsonObject> fill,
        (string RawId, int Seq)? passive,
        CancellationToken ct)
    {
        var body = new JsonObject();
        fill(body);
        if (passive is not null)
        {
            body["msg_id"] = passive.Value.RawId;
            body["msg_seq"] = passive.Value.Seq;
        }

        var url = $"{ApiBase}/v2/{(isGroup ? "groups" : "users")}/{Uri.EscapeDataString(openId)}/messages";
        var (ok, text, status) = await SendApiAsync(url, body, ct).ConfigureAwait(false);
        if (!ok)
        {
            Log($"发消息失败 HTTP {status}：{Snippet(text, 300)}");
            return new SendResult(false);
        }

        var rawReplyId = Text(JsonNode.Parse(text)?["id"]);
        return new SendResult(true, string.IsNullOrEmpty(rawReplyId) ? 0 : _ids.AliasFor(rawReplyId));
    }

    /// <summary>发一条富媒体消息（<c>msg_type=7</c> + <c>media.file_info</c>）。</summary>
    private async Task<bool> SendMediaAsync(bool isGroup, string openId, string fileInfo, CancellationToken ct)
    {
        var passive = TakePassiveRef(openId);
        var body = new JsonObject
        {
            ["msg_type"] = 7,
            ["media"] = new JsonObject { ["file_info"] = fileInfo },
        };

        if (passive is not null)
        {
            body["msg_id"] = passive.Value.RawId;
            body["msg_seq"] = passive.Value.Seq;
        }

        var url = $"{ApiBase}/v2/{(isGroup ? "groups" : "users")}/{Uri.EscapeDataString(openId)}/messages";
        var (ok, text, status) = await SendApiAsync(url, body, ct).ConfigureAwait(false);
        if (!ok)
        {
            Log($"发富媒体消息失败 HTTP {status}：{Snippet(text, 300)}");
        }

        return ok;
    }

    /// <summary>
    /// 上传富媒体（语音 3 / 图片 1），返回可直接塞进 <c>media.file_info</c> 的串。
    /// 走 <c>file_data</c>（base64 内联）：我们的 TTS 在内网，官方平台拉不到那个 URL。
    /// </summary>
    private async Task<(string? FileInfo, string? Error)> UploadAsync(
        bool isGroup,
        string openId,
        int fileType,
        byte[] data,
        string fileName,
        CancellationToken ct)
    {
        if (data.Length > MaxUploadBytes)
        {
            return (null, $"文件 {data.Length} 字节超过上限 {MaxUploadBytes}（超了会被降级成普通文件，就不是语音条了）");
        }

        var body = new JsonObject
        {
            ["file_type"] = fileType,
            ["file_data"] = Convert.ToBase64String(data),
            ["srv_send_msg"] = false,
        };

        var url = $"{ApiBase}/v2/{(isGroup ? "groups" : "users")}/{Uri.EscapeDataString(openId)}/files";
        var (ok, text, status) = await SendApiAsync(url, body, ct).ConfigureAwait(false);
        if (!ok)
        {
            // 850019 / 40034002 = 富媒体格式不支持（语音最常见的原因：不是腾讯 SILK）
            return (null, $"HTTP {status}：{Snippet(text, 300)}");
        }

        var json = JsonNode.Parse(text);
        var fileInfo = Text(json?["file_info"]);
        if (string.IsNullOrEmpty(fileInfo))
        {
            return (null, $"上传没回 file_info：{Snippet(text, 200)}");
        }

        var ttl = AsInt(json?["ttl"]);
        Log($"富媒体已上传（{fileName}，file_type={fileType}，{data.Length} 字节，ttl={ttl?.ToString() ?? "?"}s）");
        return (fileInfo, null);
    }

    private async Task<(bool Ok, string Text, int Status)> SendApiAsync(string url, JsonObject body, CancellationToken ct)
    {
        var token = await EnsureTokenAsync(false, ct).ConfigureAwait(false);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(body) };
            Auth(req, token);

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;

            // token 过期（11244/11253 之类）就地刷一次再重试，免得整条消息丢掉
            if (!resp.IsSuccessStatusCode && (text.Contains("11244", StringComparison.Ordinal) || text.Contains("11253", StringComparison.Ordinal)))
            {
                Log("token 被拒 → 刷一次 token 重试");
                token = await EnsureTokenAsync(true, ct).ConfigureAwait(false);
                using var retry = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(body) };
                Auth(retry, token);
                using var resp2 = await _http.SendAsync(retry, ct).ConfigureAwait(false);
                return (resp2.IsSuccessStatusCode, await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false), (int)resp2.StatusCode);
            }

            return (resp.IsSuccessStatusCode, text, status);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static void Auth(HttpRequestMessage req, string token)
    {
        // 官方要的是 "QQBot {token}"，不是 Bearer（写成 Bearer 会被 1127 系列拒掉）
        req.Headers.TryAddWithoutValidation("Authorization", "QQBot " + token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static StringContent JsonBody(JsonObject body)
        => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private async Task<byte[]?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log($"取音频失败 HTTP {(int)resp.StatusCode}：{Snippet(detail, 200)}");
                return null;
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length > MaxUploadBytes ? null : bytes;
        }
        catch (Exception ex)
        {
            Log($"取音频异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>给 TTS 地址补一个 <c>format=</c>（已带就不动）——官方通道要 silk，别的调用方自己定。</summary>
    private static string EnsureFormat(string url, string format)
        => url.Contains("format=", StringComparison.OrdinalIgnoreCase) ? url : url + "&format=" + format;

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

    /// <summary>被动回复引用：窗口内返回（原始 msg_id, 递增 seq），否则 null（发主动消息）。</summary>
    private (string RawId, int Seq)? TakePassiveRef(string openId)
    {
        if (!_lastInbound.TryGetValue(openId, out var last) || Clock.Now - last.At > PassiveWindow)
        {
            return null;
        }

        var seq = _msgSeq.AddOrUpdate(last.RawId, 1, (_, used) => used + 1);
        return (last.RawId, seq);
    }

    private void RememberInbound(string openId, string rawMessageId)
    {
        _lastInbound[openId] = (rawMessageId, Clock.Now);
        if (_lastInbound.Count > 500)
        {
            var cutoff = Clock.Now - TimeSpan.FromHours(2);
            foreach (var (key, value) in _lastInbound)
            {
                if (value.At < cutoff)
                {
                    _lastInbound.TryRemove(key, out _);
                }
            }
        }
    }

    /// <summary>官方会对同一条消息重复推送（尤其断线重连后）—— 去掉重复，别让模型答两遍。</summary>
    private bool IsDuplicate(string rawMessageId)
    {
        lock (_seenGate)
        {
            if (!_seen.Add(rawMessageId))
            {
                return true;
            }

            _seenOrder.Enqueue(rawMessageId);
            while (_seenOrder.Count > SeenCapacity)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }

            return false;
        }
    }

    private string SessionId
    {
        get => _sessionId;
        set
        {
            lock (_stateGate)
            {
                _sessionId = value;
            }
        }
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

    private static DateTimeOffset ParseTime(JsonNode? node)
        => DateTimeOffset.TryParse(Text(node), out var parsed) ? parsed : Clock.Now;

    private static string Mask(string value)
        => string.IsNullOrEmpty(value) ? "(空)" : value.Length <= 8 ? "***" : value[..4] + "***" + value[^2..];

    private static string Snippet(string text, int max)
        => string.IsNullOrEmpty(text) ? "(空)" : text.Length <= max ? text : text[..max] + "…";

    private void Log(string message) => _log(message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _cts?.Dispose();
        _sendGate.Dispose();
        // 出网客户端由装配点持有并统一释放（见 HttpFetcher），这里只收自己的东西
    }
}
