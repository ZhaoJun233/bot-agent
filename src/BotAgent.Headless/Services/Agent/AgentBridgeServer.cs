using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BotAgent.Services.Agent;
using BotAgent.Domain.Agent;
using System.Text.Json.Nodes;

namespace BotAgent.Services.Agent;

/// <summary>
/// 本机 Agent 桥：让 QQ 群里一条 <c>//开头的消息</c> 真正跑到**管理员本机**的 pi 上。
///
/// 为什么是这个拓扑（画出来就是设计说明书）：
/// <code>
///   群里的 //消息
///        │  (QQ)
///        ▼
///   机器人（服务器容器，没有管理员的代码/工具/pi 登录态）
///        │  WebSocket：机器人**监听**，本机主动连进来（本机在 NAT 后面，只能它出站）
///        ▼
///   本机 pi-bridge.py
///        │  subprocess
///        ▼
///   pi -p --mode json …（在读管理员自己的目录里干活）
/// </code>
///
/// 安全边界（这个功能能在别人电脑上执行命令，所以每一步都要说得清）：
///   • 只有 `//` 开头、且发送者 QQ 在 <see cref="AppSettings.AgentAllowedUsers"/> 里、且会话在白名单 → 才会生成任务；
///   • 桥连接必须带对令牌（<see cref="AppSettings.AgentToken"/>）；**没设令牌 = 拒绝所有连接**；
///   • 任务是一次性的（`pi -p`），不提供通用的“执行任意命令”接口 —— 命令只能由 pi 自己按提示词决定；
///   • 超时杀进程、单会话排队上限、结果长度截断（免得一条命令把群刷满）。
/// </summary>
public sealed class AgentBridgeServer
{
    // 配置读取入口：指向**当前发布版**（热更新是换引用，见 SettingsBox）——不要改成缓存实例。
    private AppSettings _settings => _box.Current;

    private readonly SettingsBox _box;
    private readonly Action<string> _log;

    // ---- 桥连接（支持多台设备，靠 hello 里的 host 当名字）----
    private sealed class BridgeConnection
    {
        public required WebSocket Socket { get; init; }
        public required CancellationTokenSource Lifetime { get; init; }
        public required CancellationToken Token { get; init; }
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public bool Registered { get; set; }
        public string Name { get; set; } = "device";

        public void Stop()
        {
            try { Lifetime.Cancel(); } catch (ObjectDisposedException) { }
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
        }
        public string? Cwd { get; set; }
        public string? PiVersion { get; set; }

        /// <summary>这台设备（pi）能用的模型：provider/model。面板里直接选。</summary>
        public string[] Models { get; set; } = Array.Empty<string>();

        /// <summary>设备上已有的 pi 会话（名/文件/时间/首句），面板里可接用/删除。</summary>
        public List<JsonObject> PiSessions { get; set; } = new();

        public DateTimeOffset Since { get; init; } = Clock.Now;
    }

    private readonly ConcurrentDictionary<string, BridgeConnection> _bridges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前在线的外部设备名（面板 / //status 用）。</summary>
    public IReadOnlyList<string> BridgeNames => _bridges.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    // ---- 任务队列（单工人：本机 pi 串行跑，避免同一台机器上几个 agent 抢同一个工作区）----
    private readonly ConcurrentQueue<AgentTask> _queue = new();
    private readonly object _stateLock = new();
    private AgentTask? _current;
    private bool _workerRunning;
    private int _sequence;

    /// <summary>桥侧回传的“任务进度”文本（工具调用、还在跑）。BotAgentHost 拿它决定要不要在群里说一声。</summary>
    public event Action<AgentTask>? Progress;

    /// <summary>任务结束（成功/失败/超时都走这里）。</summary>
    public event Action<AgentTask>? Finished;

    public AgentBridgeServer(SettingsBox box, Action<string> log)
    {
        _box = box;
        _log = log;
    }

    private const int HeartbeatSeconds = 30;
    private const int SendTimeoutSeconds = 10;

    // One awaited heartbeat loop per connection: no overlapping timer callbacks or orphan timers.
    private async Task HeartbeatAsync(BridgeConnection conn)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(conn.Token))
            {
                if (conn.Registered && !await SendAsync(conn, new JsonObject { ["type"] = "ping" }))
                    return;
            }
        }
        catch (OperationCanceledException) when (conn.Token.IsCancellationRequested) { }
    }

    /// <summary>桥是否在线（任意一台）。</summary>
    public bool Connected => !_bridges.IsEmpty;

    /// <summary>指定设备是否在线（传空 = 任意一台在线）。</summary>
    public bool IsDeviceOnline(string? name)
        => string.IsNullOrWhiteSpace(name) ? Connected : _bridges.ContainsKey(name.Trim());

    /// <summary>任意一台在线设备的（名字、目录、pi 版本）；都没有就返回 null。</summary>
    public (string Name, string? Cwd, string? Pi)? AnyBridge
    {
        get
        {
            foreach (var (name, conn) in _bridges)
            {
                return (name, conn.Cwd, conn.PiVersion);
            }

            return null;
        }
    }

    /// <summary>指定设备的详情（//status 面板/群里显示：pi 版本 + 目录）。</summary>
    public (string Name, string? Cwd, string? Pi)? DeviceInfo(string name)
        => _bridges.TryGetValue(name, out var conn) ? (conn.Name, conn.Cwd, conn.PiVersion) : null;

    /// <summary>指定设备上报的 pi 会话列表（没指定就取第一台在线的）。</summary>
    public List<JsonObject> DevicePiSessions(string? name = null)
        => PickBridge(name)?.PiSessions ?? new List<JsonObject>();

    /// <summary>指定设备上报的模型列表（没指定就取第一台在线的）。</summary>
    public string[] DeviceModels(string? name = null)
    {
        var conn = PickBridge(name);
        return conn?.Models ?? Array.Empty<string>();
    }

    /// <summary>让设备现场重新问一遍 pi 的模型列表（面板“刷新模型”用）。</summary>
    public Task<bool> RequestModelsAsync(string? name = null)
        => SendAsync(new JsonObject { ["type"] = "models" }, name);

    /// <summary>要下发任务时选哪台设备：指定名字就用它，否则第一台在线的。</summary>
    private BridgeConnection? PickBridge(string? preferName)
    {
        if (!string.IsNullOrWhiteSpace(preferName))
        {
            return _bridges.TryGetValue(preferName.Trim(), out var named) ? named : null;
        }

        foreach (var conn in _bridges.Values)
        {
            return conn;
        }

        return null;
    }

    /// <summary>已下发、还没收到 done/error 的任务（id → 任务）。丢过结果就是靠它修的。</summary>
    private readonly ConcurrentDictionary<string, AgentTask> _outstanding = new();

    /// <summary>在跑的任务快照（面板/状态接口用）。</summary>
    public AgentTask? Current { get { lock (_stateLock) { return _current; } } }

    public int QueuedCount => _queue.Count;

    /// <summary>
    /// 排一个任务。返回 false 表示没接（会话队列满了）。
    /// </summary>
    public bool TryEnqueue(AgentTask task)
    {
        if (!Connected)
        {
            task.Fail("本机的 agent 桥没连上");
            Finished?.Invoke(task);
            return true;   // “没桥”不是队列满，交给上层去回一句人话
        }

        var sameChatQueued = _queue.Count(t => t.SourceKey == task.SourceKey);
        var maxQueued = Math.Clamp(_settings.AgentMaxQueued, 1, 20);
        if (sameChatQueued >= maxQueued)
        {
            return false;
        }

        _queue.Enqueue(task);
        StartWorker();
        return true;
    }

    /// <summary>取消某个会话正在跑/排队的任务，返回取消掉几个。</summary>
    public int Cancel(string sourceKey)
    {
        var cancelled = 0;

        lock (_stateLock)
        {
            if (_current is { } cur && cur.SourceKey == sourceKey)
            {
                cur.CancelRequested = true;
                cancelled++;
                // 通知桥把 pi 杀掉（不杀就等于关不掉：任务还占着本机 CPU）
                _ = SendAsync(new JsonObject { ["type"] = "cancel", ["id"] = cur.Id }, cur.DeviceName);

                // 立即收尾，而不是等桥回一句“已取消”：
                // ① 管理员发 //stop 就是要“马上停”，不能还挂着；
                // ② 挂着的话，新的任务会被“串行下发”那个门挡在外面（实测踩过）。
                cur.Fail("已取消");
                _outstanding.TryRemove(cur.Id, out _);
                _current = null;
                Finished?.Invoke(cur);
            }
        }

        // 排队中的直接摘掉（ConcurrentQueue 不能按条件删 → 重建一份）
        var keep = new List<AgentTask>();
        while (_queue.TryDequeue(out var task))
        {
            if (task.SourceKey == sourceKey && !task.CancelRequested)
            {
                task.CancelRequested = true;
                task.Fail("已取消");
                Finished?.Invoke(task);
                cancelled++;
            }
            else
            {
                keep.Add(task);
            }
        }

        foreach (var task in keep)
        {
            _queue.Enqueue(task);
        }

        // 停完之后把工人叫醒（门前可能正堵着排队的新任务）
        if (cancelled > 0 && !_queue.IsEmpty && Connected)
        {
            StartWorker();
        }

        return cancelled;
    }

    /// <summary>面板里点“测试”用的：排一个任务并等结果（不经过 QQ）。</summary>
    public async Task<AgentTask> RunDirectAsync(string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var task = NewTask("panel:test", prompt, "qqchat-panel");
        var done = new TaskCompletionSource<AgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFinished(AgentTask t)
        {
            if (ReferenceEquals(t, task))
            {
                done.TrySetResult(t);
            }
        }

        Finished += OnFinished;
        try
        {
            if (!TryEnqueue(task))
            {
                task.Fail("队列满了");
                return task;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            using var reg = linked.Token.Register(() => done.TrySetResult(task));
            return await done.Task;
        }
        finally
        {
            Finished -= OnFinished;
        }
    }

    /// <summary>建一个任务（统一填默认值，免得各处漏字段）。
    /// preferDevice = 指定外部设备名（空 = 第一台在线的）；有**设备专属配置**就覆盖全局默认。</summary>
    public AgentTask NewTask(string sourceKey, string prompt, string session, string? preferDevice = null)
    {
        var device = _settings.DeviceConfigFor(preferDevice);
        return new()
        {
            Id = $"a{Interlocked.Increment(ref _sequence)}-{Clock.Now.ToUnixTimeMilliseconds()}",
            SourceKey = sourceKey,
            Prompt = prompt,
            Instructions = string.IsNullOrWhiteSpace(_settings.AgentPrompt) ? null : _settings.AgentPrompt.Trim(),
            Session = session,
            TargetDevice = preferDevice,
            WorkDir = _settings.ResolveAgentWorkDir(preferDevice),
            Model = string.IsNullOrWhiteSpace(device?.Model) ? _settings.AgentModel : device!.Model,
            Tools = string.IsNullOrWhiteSpace(device?.Tools) ? _settings.AgentTools : device!.Tools,
            TimeoutSeconds = Math.Clamp(device is { TimeoutSeconds: > 0 } ? device.TimeoutSeconds : _settings.AgentTimeoutSeconds, 30, 7200)
        };
    }

    /// <summary>让外部设备列出它上面的 pi 会话（~/.pi/agent/sessions/*/*.jsonl）。</summary>
    public Task<bool> RequestPiSessionsAsync(string deviceName = null!)
        => SendAsync(new JsonObject { ["type"] = "sessions" }, deviceName);

    /// <summary>让外部设备把它那边的会话文件删掉（删除/清空会话时用；删不掉也不影响主流程）。</summary></summary>
    public Task<bool> ForgetSessionAsync(string piSessionId)
        => string.IsNullOrWhiteSpace(piSessionId)
            ? Task.FromResult(false)
            : SendAsync(new JsonObject { ["type"] = "forget", ["session"] = piSessionId.Trim() });

    /// <summary>踢掉某台设备的连接（面板里的“断开”）；返回是不是真踢了。</summary>
    public async Task<bool> DisconnectAsync(string? deviceName)
    {
        var conn = PickBridge(deviceName);
        if (conn is null)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(SendTimeoutSeconds));
        var entered = false;
        try
        {
            await conn.SendGate.WaitAsync(timeout.Token);
            entered = true;
            // HandleAsync owns ReceiveAsync; CloseAsync would start a competing receiver.
            await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "panel-disconnect", timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // A dead connection still needs cancellation and task cleanup.
        }
        finally
        {
            if (entered) conn.SendGate.Release();
            conn.Stop();
        }

        _log($"面板里断开了外部设备：{conn.Name}");
        return true;
    }

    // ══════════ 桥连接生命周期（由 WebUiServer 在 WS 上调用）══════════

    /// <summary>处理一条桥连接（阻塞到断开）。多台设备可以同时在线（名字取自 hello 里的 host）。</summary>
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetime.CancelAfter(TimeSpan.FromSeconds(30)); // Waiting for hello is bounded too.
        var conn = new BridgeConnection { Socket = socket, Lifetime = lifetime, Token = lifetime.Token };
        var heartbeat = HeartbeatAsync(conn);

        _log("agent 桥已连接，等待 hello");

        try
        {
            var buffer = new byte[16 * 1024];
            using var pending = new MemoryStream();
            while (socket.State == WebSocketState.Open && !conn.Token.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), conn.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                pending.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(pending.ToArray());
                pending.SetLength(0);
                HandleBridgeMessage(conn, json);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            _log($"agent 桥连接出错: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            conn.Stop();
            lock (_stateLock)
            {
                // Removing by both key and instance cannot evict a same-name replacement.
                ((ICollection<KeyValuePair<string, BridgeConnection>>)_bridges)
                    .Remove(new(conn.Name, conn));
            }
            FailConnectionTasks(conn);
            await heartbeat;
            socket.Dispose();
            _log($"agent 桥已断开（剩 {_bridges.Count} 台在线）");
            if (!_queue.IsEmpty && Connected) StartWorker();
        }
    }

    private void FailConnectionTasks(BridgeConnection conn)
    {
        var failed = new List<AgentTask>();
        lock (_stateLock)
        {
            foreach (var task in _outstanding.Values.Where(t => t.ConnectionId == conn.Id))
            {
                if (task.Done) continue;
                task.Fail("外部 agent 设备断开了");
                _outstanding.TryRemove(task.Id, out _);
                if (ReferenceEquals(_current, task)) _current = null;
                failed.Add(task);
            }
        }
        foreach (var task in failed) Finished?.Invoke(task);
    }

    private void HandleBridgeMessage(BridgeConnection conn, string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            _log("agent 桥发来的不是 JSON，已忽略");
            return;
        }

        var type = node?["type"]?.GetValue<string>();
        var id = node?["id"]?.GetValue<string>();
        if (conn.Token.IsCancellationRequested) return;
        if (type != "hello" && (!conn.Registered ||
            !_bridges.TryGetValue(conn.Name, out var active) || !ReferenceEquals(active, conn))) return;
        if (type is "started" or "progress" or "chunk" or "done" or "error")
        {
            if (FindTask(id) is not { } owned || owned.ConnectionId != conn.Id) return;
        }

        switch (type)
        {
            case "hello":
            {
                if (conn.Registered) return;
                var name = node?["host"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    conn.Stop();
                    return;
                }
                conn.Name = name;

                conn.Cwd = node?["cwd"]?.GetValue<string>();
                conn.PiVersion = node?["pi"]?.GetValue<string>();
                if (node?["models"] is JsonArray models)
                {
                    conn.Models = models.Select(m => m?.GetValue<string>() ?? string.Empty)
                        .Where(m => m.Length > 0)
                        .ToArray();
                }

                BridgeConnection? previous;
                lock (_stateLock)
                {
                    if (conn.Token.IsCancellationRequested) return;
                    _bridges.TryGetValue(conn.Name, out previous);
                    conn.Registered = true;
                    conn.Lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    _bridges[conn.Name] = conn;
                }
                if (previous is not null && !ReferenceEquals(previous, conn))
                {
                    previous.Stop();
                    FailConnectionTasks(previous);
                }
                StartWorker();

                _log($"agent 桥握手：{conn.Name}，目录 {conn.Cwd ?? "?"}，pi {conn.PiVersion ?? "?"}，" +
                     $"模型 {conn.Models.Length} 个（在线 {_bridges.Count} 台）");
                return;
            }

            case "models":
            {
                if (node?["models"] is JsonArray list)
                {
                    conn.Models = list.Select(m => m?.GetValue<string>() ?? string.Empty)
                        .Where(m => m.Length > 0)
                        .ToArray();
                    _log($"设备 {conn.Name} 上报模型 {conn.Models.Length} 个");
                }

                return;
            }

            case "sessions":
            {
                if (node?["list"] is JsonArray sessions)
                {
                    conn.PiSessions = sessions.OfType<JsonObject>().ToList();
                    _log($"设备 {conn.Name} 上报 pi 会话 {conn.PiSessions.Count} 个");
                }

                return;
            }

            case "started":
                _log($"agent 任务开始执行：#{id}");
                return;

            case "progress":
            {
                if (FindTask(id) is not { } task)
                {
                    return;
                }

                task.LastNote = node?["note"]?.GetValue<string>();
                Progress?.Invoke(task);
                return;
            }

            case "chunk":
            {
                if (FindTask(id) is not { } task)
                {
                    return;
                }

                var text = node?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text))
                {
                    // 只留最近的尾巴（pi 的流式输出可能很长，全存没意义）：用于“过程里最后一句”兜底
                    task.StreamTail = Tail(task.StreamTail + text, 4000);
                }

                return;
            }

            case "done":
            {
                if (FindTask(id) is null)
                {
                    return;
                }

                SettleTask(id, node?["text"]?.GetValue<string>(), node?["exitCode"]?.GetValue<int>() ?? 0,
                    node?["durationMs"]?.GetValue<long>() ?? 0, node?["toolCalls"]?.GetValue<int>() ?? 0);
                return;
            }

            case "error":
                SettleTask(id, null, node?["exitCode"]?.GetValue<int>() ?? -1, 0, 0,
                    node?["message"]?.GetValue<string>() ?? "本机 agent 报错");
                return;

            case "pong":
                return;

            default:
                _log($"agent 桥发来未知消息类型 {type}（已忽略）");
                return;
        }
    }

    private AgentTask? FindTask(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        // 先查“已下发但还没收尾”的表：
        //   线上踩过 —— 以前只查 _current + 队列，而 _current 会被下一个任务顶掉，
        //   于是先发的那个任务的 done/error 到了也找不到它的属主，结果就**静默丢掉**
        //   （群里表现为“跑完了但一直没声”= 卡住）。
        if (_outstanding.TryGetValue(id, out var tracked))
        {
            return tracked;
        }

        lock (_stateLock)
        {
            if (_current is { } cur && cur.Id == id)
            {
                return cur;
            }
        }

        return _queue.FirstOrDefault(t => t.Id == id);
    }

    private void SettleTask(string? id, string? text, int exitCode, long durationMs, int toolCalls, string? error = null)
    {
        AgentTask? task;
        lock (_stateLock)
        {
            if (id is null || !_outstanding.TryGetValue(id, out task) || task.Done) return;
            var body = (text ?? string.Empty).Trim();
            if (body.Length == 0) body = (task.StreamTail ?? string.Empty).Trim();
            task.DurationMs = durationMs > 0 ? durationMs : (long)(Clock.Now - task.StartedAt).TotalMilliseconds;
            task.ToolCalls = toolCalls;
            if (error is not null || exitCode != 0)
            {
                var detail = error is { Length: > 0 } ? error : $"pi 退出码 {exitCode}";
                task.Fail(body.Length > 0 ? detail + "：" + body : detail);
            }
            else if (body.Length == 0) task.Fail("本机 agent 没有输出（可能被工具白名单/权限挡了）");
            else task.Succeeded(body);
            _outstanding.TryRemove(task.Id, out _);
            if (ReferenceEquals(_current, task)) _current = null;
        }
        Finished?.Invoke(task);
        if (!_queue.IsEmpty && Connected) StartWorker();
    }

    // ══════════ 工人（把队列里的任务一条条喂给桥）══════════

    private void StartWorker()
    {
        lock (_stateLock)
        {
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }

        _ = Task.Run(RunWorkerAsync);
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (true)
            {
                if (!Connected)
                {
                    return;   // 桥不在：任务留在队列里等重连
                }

                // 外部设备上的 pi 是**串行**跑的：上一个还没收尾就不能下发下一个，
                // 否则桥会回“本机还有任务在跑（串行执行）”，管理员看到的就是“发了两条，一条报错”。
                lock (_stateLock)
                {
                    if (_current is { } running && !running.Done)
                    {
                        // 安全网：万一结果帧真丢了（网断、桥崩且没发断连），不能让后来的任务永远等下去
                        var age = Clock.Now - running.StartedAt;
                        if (age.TotalSeconds > running.TimeoutSeconds + 60)
                        {
                            running.Fail($"超时未返回（已等 {age.TotalSeconds:F0}s）");
                            Finished?.Invoke(running);
                            _outstanding.TryRemove(running.Id, out _);
                            _current = null;
                        }
                        else
                        {
                            // 等它收尾（150ms 一轮，开销可忽略）
                            _ = WaitForCurrentAsync();
                            return;
                        }
                    }
                }

                if (!_queue.TryDequeue(out var task))
                {
                    return;
                }

                if (task.CancelRequested)
                {
                    task.Fail("已取消");
                    Finished?.Invoke(task);
                    continue;
                }

                // Select a concrete connection; never re-resolve by name while sending.
                var target = PickBridge(task.TargetDevice);
                if (target is null)
                {
                    lock (_stateLock)
                    {
                        _current = null;
                    }

                    task.Fail(string.IsNullOrWhiteSpace(task.TargetDevice)
                        ? "没有在线的外部 agent 设备"
                        : $"指定的外部设备「{task.TargetDevice}」不在线");
                    Finished?.Invoke(task);
                    continue;
                }

                task.DeviceName = target.Name;
                task.ConnectionId = target.Id;
                task.WorkDir = _settings.ResolveAgentWorkDir(target.Name, target.Cwd);
                task.StartedAt = Clock.Now;
                // 设备专属配置在这里最后盖一道（防止调用方没传设备名/竞态）：
                // 面板里给某台设备单独配的 模型/目录/工具/超时 以此为准。
                if (_settings.DeviceConfigFor(target.Name) is { } deviceCfg)
                {
                    if (!string.IsNullOrWhiteSpace(deviceCfg.WorkDir)) task.WorkDir = deviceCfg.WorkDir;
                    if (!string.IsNullOrWhiteSpace(deviceCfg.Tools)) task.Tools = deviceCfg.Tools;
                    if (deviceCfg.TimeoutSeconds > 0)
                    {
                        task.TimeoutSeconds = Math.Clamp(deviceCfg.TimeoutSeconds, 30, 7200);
                    }

                    if (!string.IsNullOrWhiteSpace(deviceCfg.Model))
                    {
                        // 模型名要先跟**这台设备自己上报的列表**对一下：
                        // pi 只认 provider/model（如 localhost/gpt-oss-120b-medium），
                        // 把聊天网关的模型名（比如 gpt-oss-120b-medium）填进来，pi 会直接
                        // “Model xxx not found” 报错 —— 群里看到的就是“外部 Agent 报错”（2026-09-17 实际踩过）。
                        var known = target.Models;
                        if (known.Length > 0 && !known.Contains(deviceCfg.Model, StringComparer.OrdinalIgnoreCase))
                        {
                            task.ModelFallbackFrom = deviceCfg.Model;
                            task.Model = null;   // 用 pi 自己的默认，并告知（不能让一个填错的名字把活卡死）
                            _log($"设备 {target.Name} 配置的模型「{deviceCfg.Model}」不在它上报的列表里 → 这次用 pi 默认模型");
                        }
                        else
                        {
                            task.Model = deviceCfg.Model;
                        }
                    }
                }

                var staleTarget = false;
                lock (_stateLock)
                {
                    if (target.Token.IsCancellationRequested ||
                        !_bridges.TryGetValue(target.Name, out var live) || !ReferenceEquals(live, target))
                    {
                        staleTarget = true;
                    }
                    else
                    {
                        _current = task;
                        _outstanding[task.Id] = task;
                    }
                }

                if (staleTarget)
                {
                    // 就在这一瞬间桥断了：任务回队列（不能丢），但**不能**立刻再挑一次 ——
                    // 那条连接要等 HandleAsync 的 finally 才从 _bridges 里摘掉，空转会烧 CPU。
                    _queue.Enqueue(task);
                    await Clock.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
                    continue;
                }
                _log($"agent 任务下发：{task.SourceKey} #{task.Id} → {target.Name}（{Shorten(task.Prompt, 60)}）" +
                     (task.WorkDir is { Length: > 0 } ? $"，目录 {task.WorkDir}" : string.Empty));

                var sent = await SendAsync(target, new JsonObject
                {
                    ["type"] = "task",
                    ["id"] = task.Id,
                    ["prompt"] = task.Prompt,
                    // 附加提示词单独一栏（桥那边走 --append-system-prompt），不要拼进 prompt
                    ["instructions"] = task.Instructions ?? string.Empty,
                    ["session"] = task.Session,
                    ["cwd"] = task.WorkDir ?? string.Empty,
                    ["model"] = task.Model ?? string.Empty,
                    ["tools"] = task.Tools ?? string.Empty,
                    ["timeoutSec"] = task.TimeoutSeconds
                });

                if (!sent)
                {
                    FailConnectionTasks(target);
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _workerRunning = false;
            }

            // 队列里还有活儿 + 桥还在 → 继续（比如刚下发完一个又来了新的）
            if (!_queue.IsEmpty && Connected && Current is null)
            {
                StartWorker();
            }
        }
    }

    /// <summary>等在跑的任务收尾；收尾后自己回到工人循环（不用轮询打断）。</summary>
    private async Task WaitForCurrentAsync()
    {
        try
        {
            await Clock.Delay(150);
            if (!_queue.IsEmpty && Connected)
            {
                StartWorker();
            }
        }
        catch
        {
            // 忽略：下一轮消息/事件还会再唤醒
        }
    }

    private Task<bool> SendAsync(JsonObject payload, string? deviceName = null)
    {
        var conn = PickBridge(deviceName);
        return conn is null ? Task.FromResult(false) : SendAsync(conn, payload);
    }

    private static async Task<bool> SendAsync(BridgeConnection conn, JsonObject payload)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(conn.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(SendTimeoutSeconds));
        var entered = false;
        try
        {
            await conn.SendGate.WaitAsync(timeout.Token);
            entered = true;
            if (conn.Socket.State != WebSocketState.Open || conn.Token.IsCancellationRequested) return false;
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            await conn.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            conn.Stop(); // Cancel ReceiveAsync too; its finally owns removal and task cleanup.
            return false;
        }
        finally
        {
            if (entered) conn.SendGate.Release();
        }
    }

    /// <summary>给状态接口/面板用的一句话摘要。</summary>
    public string Describe()
    {
        if (!_settings.EnableAgentBridge)
        {
            return "未启用（面板里打开「本机 Agent」）";
        }

        if (!Connected)
        {
            return "未连接（外部 agent 设备没连上）";
        }

        var devices = _bridges.ToArray()
            .Select(kv => $"{kv.Key}（pi {kv.Value.PiVersion ?? "?"}，目录 {_settings.ResolveAgentWorkDir(kv.Key, kv.Value.Cwd) ?? "?"}）")
            .ToList();

        var current = Current;
        var running = current is null
            ? "空闲"
            : $"在跑 #{current.Id}→{(string.IsNullOrWhiteSpace(current.DeviceName) ? "服务器" : current.DeviceName)}（{Shorten(current.Prompt, 24)}）";

        return $"在线设备 {_bridges.Count} 台：{string.Join("、", devices)} + {running}" +
               (QueuedCount > 0 ? $"，排队 {QueuedCount}" : string.Empty);
    }

    private static string Tail(string text, int max)
        => text.Length <= max ? text : text[^max..];

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

/// <summary>一个 agent 任务的全生命周期（桥侧执行、结果回群）。</summary>
public sealed class AgentTask
{
    public required string Id { get; init; }

    /// <summary>哪个会话提的（群聊 group:123 / 私聊 private:456）。</summary>
    public required string SourceKey { get; init; }

    public required string Prompt { get; init; }

    /// <summary>
    /// 附加提示词（面板里的「Agent 附加提示词」，默认 = 隐私红线）：桥会把它当 pi 的
    /// **system prompt**（<c>--append-system-prompt</c>）传下去，**不拼进任务正文**。
    /// 为什么不拼：拼在一起时模型容易把规则当成任务（回复“收到，我按红线执行”却不干活）；
    /// 服务器内置 agent 那份也是拼进它自己的 system prompt。
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>这个任务派给哪台设备（空 = 第一台在线的）；服务器内置 agent 不走这里。</summary>
    public string? TargetDevice { get; init; }

    /// <summary>实际执行它的外部设备名（服务器内置 agent 时为空）。</summary>
    internal string? ConnectionId { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>要不要把 pi 的会话名传下去：同一个群用同一个 → agent 记得上一轮（<c>--session-id</c>）。
    /// 服务器内置 agent 不用它（它自己拿任务提示词从零开始）。</summary>
    public required string Session { get; init; }

    public string? WorkDir { get; set; }

    public string? Model { get; set; }

    public string? Tools { get; set; }

    public int TimeoutSeconds { get; set; }

    /// <summary>配置里那个模型名在这台设备上不存在时，记下原值（本轮的降级要告知管理员）。</summary>
    public string? ModelFallbackFrom { get; set; }

    /// <summary>这一轮对应的“小会话”记录 id（跑完回写结果）。</summary>
    public string? RunId { get; set; }

    /// <summary>属于哪个 agent 会话（切换/新建会话就认它）。</summary>
    public AgentSession? SessionRef { get; set; }

    /// <summary>服务器内置后端：这一轮之前的历史（同一会话的上几轮）。</summary>
    public List<(string Role, string Text)>? History { get; set; }

    /// <summary>
    /// 服务器内置后端：这一轮**要不要**把历史喂给模型。
    ///
    /// 默认 false（每条 // 指令单独对待）—— 管理员 2026-09-18 实测：会话里堆着上几轮的指令原文时，
    /// 模型会把“查服务器状态”这种旧指令也答一遍，新指令的回复里混进旧内容。
    /// 想要“接着上一句聊”就把面板那个开关打开，或单条写 <c>//接着 …</c>。
    /// </summary>
    public bool UseHistory { get; set; }

    /// <summary>服务器内置后端：跑完后的完整对话（上层存回会话）。</summary>
    public List<(string Role, string Text)>? Conversation { get; set; }
    /// <summary>
    /// 服务器内置后端的 **QQ 动作现场**（当前会话、发指令的人、本条消息 id）——
    /// 由 BotAgentHost 按任务塞进来；外部设备那条路不用（它在自己电脑上跑，碰不到协议端），面板“试一条”也没有。
    /// </summary>
    public IQqActionHost? QqHost { get; set; }

    public DateTimeOffset StartedAt { get; set; } = Clock.Now;

    public long DurationMs { get; set; }

    public bool Done { get; private set; }

    public bool Ok { get; private set; }

    public string? Text { get; private set; }

    public string? Error { get; private set; }

    /// <summary>桥侧最近一次“我在干什么”（例如 🔧 bash）——群里报进度用。</summary>
    public string? LastNote { get; set; }

    public string? StreamTail { get; set; }

    public int ToolCalls { get; set; }

    public bool CancelRequested { get; set; }

    public void Succeeded(string text)
    {
        Done = true;
        Ok = true;
        Text = text;
    }

    public void Fail(string error)
    {
        Done = true;
        Ok = false;
        Error = error;
    }
}
