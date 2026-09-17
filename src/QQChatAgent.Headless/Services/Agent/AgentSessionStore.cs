using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// **Agent 执行会话**：每个聊天（群/好友）下面直接挂一串执行会话。
///
/// 号主 2026-09-17 定的口径（走过一次两层模型，被否了）：
///   “干脆不给群聊单独会话，直接改为统一 Agent 执行会话” ——
///   所以这里**只有一层**：会话 = 执行会话（话题/档位/分组都不需要）。
///   每个会话自己带：名字（自动标题/可改名）、后端（外部设备 / 服务器内置）、
///   独立上下文（外部 = pi 的 session-id；内置 = 我们存的对话）、执行流水（每次任务的记录）。
///
/// 与群聊会话对齐的操作：新建（//new）、切换（//use）、改名（//rename）、删除（//del）、清空（//reset）、
/// 以及“把 pi 里已有的会话接过来”（//pi 列出来 → //import）。
///
/// 存储：`&lt;dataDir&gt;/agent-sessions.json`。上限见常量。并发一把锁，落盘失败只记日志。
/// </summary>
public sealed class AgentSessionStore
{
    /// <summary>服务器内置后端：单个会话最多留多少轮对话。</summary>
    private const int MaxTurns = 40;

    /// <summary>每个聊天最多几个执行会话（超出时删最久没动的，当前会话不动）。</summary>
    public const int MaxSessionsPerChat = 12;

    /// <summary>单个会话历史最多多少字（超了从头砍）。</summary>
    private const int MaxHistoryChars = 40000;

    /// <summary>每个会话最多留多少条执行流水。</summary>
    private const int MaxRunsPerSession = 20;

    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private readonly Dictionary<string, ChatSessions> _chats = new(StringComparer.Ordinal);

    /// <summary>设备上报的 pi 会话（面板/命令里“从 pi 导入”用；不落盘，重启后现拉）。</summary>
    private readonly Dictionary<string, List<JsonObject>> _piSessions = new(StringComparer.OrdinalIgnoreCase);

    public AgentSessionStore(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>一个执行会话。</summary>
    public sealed class AgentSession
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "会话1";

        /// <summary>名字是自动总结的（第一句话会给它起标题；手动改过就不动）。</summary>
        public bool AutoNamed { get; set; } = true;

        /// <summary>host = 外部设备上的 pi；server = 服务器内置工具循环。</summary>
        public string Backend { get; set; } = "host";

        /// <summary>外部后端：具体哪台设备（空 = 当前在线的第一台）。</summary>
        public string? Device { get; set; }

        /// <summary>外部后端：pi 那边的 session-id。</summary>
        public string PiSessionId { get; set; } = string.Empty;

        /// <summary>这个 pi 会话是我们建的吗（false = 从 pi 里导入的，删的时候要小心）。</summary>
        public bool PiOwned { get; set; } = true;

        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

        /// <summary>服务器内置：这段上下文的轮数。</summary>
        public int Turns { get; set; }

        /// <summary>服务器内置：对话历史（外部后端由 pi 自己存）。</summary>
        public List<HistoryEntry> History { get; set; } = new();

        /// <summary>这个会话里每次任务的流水（小会话级的“执行记录”）。</summary>
        public List<SessionRun> Runs { get; set; } = new();
    }

    public sealed class HistoryEntry
    {
        public string Role { get; set; } = "user";
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>一次任务执行流水。</summary>
    public sealed class SessionRun
    {
        public required string Id { get; init; }
        public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
        public string Prompt { get; set; } = string.Empty;
        public bool? Ok { get; set; }                    // null = 还在跑
        public long DurationMs { get; set; }
        public int ToolCalls { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? Device { get; set; }
        public string? PiSession { get; set; }
    }

    private sealed class ChatSessions
    {
        /// <summary>backend → 当前会话 id（两个后端各自记一个当前）。</summary>
        public Dictionary<string, string> Current { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AgentSession> Sessions { get; set; } = new();
    }

    // ══════════ 查询 ══════════

    /// <summary>某个聊天的全部执行会话（新的在前）。</summary>
    public List<AgentSession> List(string sourceKey)
    {
        lock (_gate)
        {
            return _chats.TryGetValue(sourceKey, out var chat)
                ? chat.Sessions.OrderByDescending(s => s.UpdatedAt).ToList()
                : new List<AgentSession>();
        }
    }

    /// <summary>当前会话（指定后端；没有就建一个默认的）。</summary>
    public AgentSession EnsureCurrent(string sourceKey, string backend)
    {
        lock (_gate)
        {
            var chat = EnsureChat(sourceKey);
            if (chat.Current.TryGetValue(backend, out var id) &&
                chat.Sessions.FirstOrDefault(s => s.Id == id) is { } found)
            {
                return found;
            }

            var created = CreateLocked(sourceKey, chat, backend, backend == "server" ? "默认（服务器）" : "默认", null);
            created.AutoNamed = true;   // 占位名字：第一句话应该能把它变成真标题
            chat.Current[backend] = created.Id;
            Save();
            return created;
        }
    }

    /// <summary>按 id / 名字 / 序号找一个会话。</summary>
    public AgentSession? Find(string sourceKey, string idNameOrIndex)
    {
        lock (_gate)
        {
            return _chats.TryGetValue(sourceKey, out var chat) ? Resolve(chat, idNameOrIndex) : null;
        }
    }

    /// <summary>是不是当前会话。</summary>
    public bool IsCurrent(string sourceKey, AgentSession session)
    {
        lock (_gate)
        {
            return _chats.TryGetValue(sourceKey, out var chat) &&
                   chat.Current.TryGetValue(session.Backend, out var id) && id == session.Id;
        }
    }

    /// <summary>某个会话的执行流水（新的在前）。</summary>
    public List<SessionRun> Runs(string sourceKey, string sessionId)
    {
        lock (_gate)
        {
            return FindSession(sourceKey, sessionId)?.Runs.OrderByDescending(r => r.At).ToList()
                   ?? new List<SessionRun>();
        }
    }

    /// <summary>服务器内置后端的上下文（喂给模型）。</summary>
    public List<(string Role, string Text)> History(string sourceKey, string sessionId)
    {
        lock (_gate)
        {
            return FindSession(sourceKey, sessionId) is { } s
                ? s.History.Select(h => (h.Role, h.Text)).ToList()
                : new List<(string Role, string Text)>();
        }
    }

    /// <summary>外部后端跑任务要用的 pi 会话 id（空就现场分配一个）。</summary>
    public string PiSessionId(string sourceKey, string sessionId)
    {
        lock (_gate)
        {
            if (FindSession(sourceKey, sessionId) is { } s)
            {
                if (s.PiSessionId.Length == 0)
                {
                    s.PiSessionId = new PiSessionNamer(sourceKey, s.Id).Value;
                    Save();
                }

                return s.PiSessionId;
            }

            return new PiSessionNamer(sourceKey, sessionId).Value;
        }
    }

    // ══════════ 变更（新建 / 切换 / 改名 / 删除 / 清空）══════════

    public AgentSession Create(string sourceKey, string backend, string? name, string? device = null,
        string? piSessionId = null, bool piOwned = true)
    {
        lock (_gate)
        {
            var chat = EnsureChat(sourceKey);
            var created = CreateLocked(sourceKey, chat, backend, name, device, piSessionId, piOwned);
            chat.Current[backend] = created.Id;
            Save();
            return created;
        }
    }

    public bool Use(string sourceKey, string idNameOrIndex, out AgentSession? used)
    {
        lock (_gate)
        {
            used = _chats.TryGetValue(sourceKey, out var chat) ? Resolve(chat, idNameOrIndex) : null;
            if (used is null)
            {
                return false;
            }

            chat.Current[used.Backend] = used.Id;
            used.UpdatedAt = DateTimeOffset.Now;
            Save();
            return true;
        }
    }

    /// <summary>删除会话（删的正好是当前的 → 自动补一个干净的）。返回被删的（外部要用它删 pi 文件）。</summary>
    public AgentSession? Delete(string sourceKey, string idNameOrIndex)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(sourceKey, out var chat))
            {
                return null;
            }

            var found = Resolve(chat, idNameOrIndex);
            if (found is null)
            {
                return null;
            }

            chat.Sessions.Remove(found);
            if (chat.Current.TryGetValue(found.Backend, out var cur) && cur == found.Id)
            {
                chat.Current.Remove(found.Backend);
                var fresh = CreateLocked(sourceKey, chat, found.Backend,
                    found.Backend == "server" ? "默认（服务器）" : "默认", found.Device);
                fresh.AutoNamed = true;   // 同上
                chat.Current[found.Backend] = fresh.Id;
            }

            Save();
            return found;
        }
    }

    /// <summary>清空会话（内置清历史；外部换一个全新的 pi 会话 id，并让设备删掉旧的）。</summary>
    public AgentSession? Reset(string sourceKey, string idNameOrIndex)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(sourceKey, out var chat))
            {
                return null;
            }

            var session = Resolve(chat, idNameOrIndex);
            if (session is null)
            {
                return null;
            }

            session.History.Clear();
            session.Runs.Clear();
            session.Turns = 0;
            if (session.Backend != "server")
            {
                session.PiSessionId = new PiSessionNamer(sourceKey, session.Id).Value;
                session.PiOwned = true;
            }

            session.UpdatedAt = DateTimeOffset.Now;
            Save();
            return session;
        }
    }

    public bool Rename(string sourceKey, string idNameOrIndex, string newName)
    {
        lock (_gate)
        {
            var session = Find(sourceKey, idNameOrIndex);
            var name = (newName ?? string.Empty).Trim();
            if (session is null || name.Length == 0)
            {
                return false;
            }

            session.Name = name.Length > 24 ? name[..24] : name;
            session.AutoNamed = false;
            session.UpdatedAt = DateTimeOffset.Now;
            Save();
            return true;
        }
    }

    /// <summary>服务器内置后端跑完一轮：记下对话。</summary>
    public void AppendTurn(string sourceKey, string sessionId, IReadOnlyList<(string Role, string Text)> messages)
    {
        lock (_gate)
        {
            if (FindSession(sourceKey, sessionId) is not { } session)
            {
                return;
            }

            session.History.Clear();
            foreach (var (role, text) in messages)
            {
                if (text.Length > 0)
                {
                    session.History.Add(new HistoryEntry { Role = role, Text = text });
                }
            }

            var total = session.History.Sum(h => h.Text.Length);
            while (session.History.Count > 2 && (total > MaxHistoryChars || session.History.Count > MaxTurns * 2))
            {
                total -= session.History[0].Text.Length;
                session.History.RemoveAt(0);
            }

            session.Turns = session.History.Count(h => h.Role == "user");
            session.UpdatedAt = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>开始一次任务（记流水），返回 runId。</summary>
    public string StartRun(string sourceKey, string sessionId, string prompt, string? device)
    {
        lock (_gate)
        {
            if (FindSession(sourceKey, sessionId) is not { } session)
            {
                return string.Empty;
            }

            var run = new SessionRun
            {
                Id = NewId(),
                Prompt = Shorten(prompt, 80),
                Device = device ?? session.Device,
                PiSession = session.Backend == "server" ? null : session.PiSessionId
            };
            session.Runs.Insert(0, run);
            while (session.Runs.Count > MaxRunsPerSession)
            {
                session.Runs.RemoveAt(session.Runs.Count - 1);
            }

            session.UpdatedAt = DateTimeOffset.Now;
            Save();
            return run.Id;
        }
    }

    /// <summary>一次任务收尾：写回结果。</summary>
    public void FinishRun(string sourceKey, string sessionId, string runId, bool ok, long durationMs,
        int toolCalls, string result)
    {
        lock (_gate)
        {
            var run = FindSession(sourceKey, sessionId)?.Runs.FirstOrDefault(r => r.Id == runId);
            if (run is null)
            {
                return;
            }

            run.Ok = ok;
            run.DurationMs = durationMs;
            run.ToolCalls = toolCalls;
            run.Result = Shorten(result, 160);
            run.At = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>第一次真的在某个会话里干活时，用那句提示词当标题（只覆盖自动名）。</summary>
    public void TitleFromPrompt(string sourceKey, string sessionId, string prompt)
    {
        lock (_gate)
        {
            if (FindSession(sourceKey, sessionId) is not { } session || !session.AutoNamed)
            {
                return;
            }

            session.Name = AutoTitle(prompt);
            session.UpdatedAt = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>
    /// 用任务的第一句话当标题：剔掉“帮我/看看/麻烦…”这类客套话，截到 16 字。
    /// 为什么不用模型总结：起个能认出来的名字不值得多一次模型调用 + 多几秒延迟。
    /// </summary>
    public static string AutoTitle(string prompt)
    {
        var text = (prompt ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0)
        {
            return "新会话";
        }

        var prefixes = new[] { "帮我看看", "帮我看下", "帮我看一下", "看一下", "看看", "看下", "查一下", "查下", "帮我", "帮忙", "麻烦", "给我", "请", "去", "来" };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var prefix in prefixes)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length + 1)
                {
                    text = text[prefix.Length..].TrimStart();
                    changed = true;
                }
            }
        }

        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 16 ? text : text[..16] + "…";
    }

    /// <summary>所有聊天的会话总览（面板与 //sessions all 用）。</summary>
    public List<(string SourceKey, List<AgentSession> Sessions)> AllChats()
    {
        lock (_gate)
        {
            return _chats
                .Where(kv => kv.Value.Sessions.Count > 0)
                .Select(kv => (kv.Key, kv.Value.Sessions.OrderByDescending(s => s.UpdatedAt).ToList()))
                .OrderByDescending(x => x.Item2.FirstOrDefault()?.UpdatedAt ?? DateTimeOffset.MinValue)
                .ToList();
        }
    }

    // ══════════ pi 里的会话（设备上报，供“从 pi 导入”）══════════

    /// <summary>记住某台设备上报的 pi 会话列表。</summary>
    public void RememberPiSessions(string device, List<JsonObject> list)
    {
        lock (_gate)
        {
            _piSessions[device] = list.Select(x => x.DeepClone().AsObject()).ToList();
        }
    }

    /// <summary>最近一次设备上报的 pi 会话（可能空/过期）。</summary>
    public List<JsonObject> LastPiSessions(string? device = null)
    {
        lock (_gate)
        {
            if (device is { Length: > 0 } && _piSessions.TryGetValue(device, out var one))
            {
                return one.Select(x => x.DeepClone().AsObject()).ToList();
            }

            return _piSessions.Values.SelectMany(v => v).Select(x => x.DeepClone().AsObject()).ToList();
        }
    }

    // ══════════ 内部 ══════════

    /// <summary>pi 会话命名规则：`qqchat-&lt;聊天&gt;-&lt;会话id&gt;`（不对外暴露实现细节，统一从这里生成）。</summary>
    private readonly record struct PiSessionNamer(string SourceKey, string SessionId)
    {
        public string Value => $"qqchat-{SourceKey.Replace(":", "-")}-{SessionId}";
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..8];

    private static string Shorten(string text, int max)
    {
        var one = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    private ChatSessions EnsureChat(string sourceKey)
    {
        if (!_chats.TryGetValue(sourceKey, out var chat))
        {
            chat = new ChatSessions();
            _chats[sourceKey] = chat;
        }

        return chat;
    }

    private AgentSession? FindSession(string sourceKey, string sessionId)
        => _chats.TryGetValue(sourceKey, out var chat)
            ? chat.Sessions.FirstOrDefault(s => s.Id == sessionId)
            : null;

    private static AgentSession? Resolve(ChatSessions chat, string idNameOrIndex)
    {
        var key = (idNameOrIndex ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            return null;
        }

        var ordered = chat.Sessions.OrderByDescending(s => s.UpdatedAt).ToList();
        if (int.TryParse(key, out var index) && index >= 1 && index <= ordered.Count)
        {
            return ordered[index - 1];
        }

        return chat.Sessions.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? chat.Sessions.Where(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
    }

    private AgentSession CreateLocked(string sourceKey, ChatSessions chat, string backend, string? name, string? device,
        string? piSessionId = null, bool piOwned = true)
    {
        // 超出上限：删最久没动的（当前会话不动）
        if (chat.Sessions.Count >= MaxSessionsPerChat)
        {
            var keep = chat.Current.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var victim = chat.Sessions.Where(s => !keep.Contains(s.Id)).OrderBy(s => s.UpdatedAt).FirstOrDefault();
            if (victim is not null)
            {
                chat.Sessions.Remove(victim);
            }
        }

        var id = NewId();
        var session = new AgentSession
        {
            Id = id,
            Name = name is { Length: > 0 } n ? (n.Length > 24 ? n[..24] : n) : $"会话{chat.Sessions.Count + 1}",
            AutoNamed = name is not { Length: > 0 },
            Backend = backend == "server" ? "server" : "host",
            Device = device,
            PiOwned = piOwned,
            PiSessionId = backend == "server" ? string.Empty : (piSessionId ?? new PiSessionNamer(sourceKey, id).Value)
        };

        chat.Sessions.Add(session);
        return session;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_path, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("chats", out var chats) ||
                chats.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var entry in chats.EnumerateObject())
            {
                var value = entry.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var chat = new ChatSessions();
                if (value.TryGetProperty("current", out var current) && current.ValueKind == JsonValueKind.Object)
                {
                    foreach (var cur in current.EnumerateObject())
                    {
                        if (cur.Value.ValueKind == JsonValueKind.String && cur.Value.GetString() is { Length: > 0 } id)
                        {
                            chat.Current[cur.Name] = id;
                        }
                    }
                }

                if (value.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in sessions.EnumerateArray())
                    {
                        if (ReadSession(item) is { } session)
                        {
                            chat.Sessions.Add(session);
                        }
                    }
                }

                _chats[entry.Name] = chat;
            }

            _log($"执行会话已恢复：{_chats.Count} 个聊天 / {_chats.Sum(c => c.Value.Sessions.Count)} 个会话");
        }
        catch (Exception ex)
        {
            // 文件坏了就当没有（会话丢了能再建，不能因此起不来）
            _log($"执行会话文件读不了，按空的继续：{ex.Message}");
        }
    }

    /// <summary>读一条会话；兼容两种历史格式：① 扁平（现在）② 老的两层（execs[] 里放着真数据）。</summary>
    private static AgentSession? ReadSession(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = Str(item, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // 老格式：数据在 execs[] 里（一个话题下的小会话）→ 摊平成多个会话（号主要的就是扁平的）
        if (item.TryGetProperty("execs", out var execs) && execs.ValueKind == JsonValueKind.Array)
        {
            var parentName = Str(item, "name") ?? "会话";
            var backend = Str(item, "backend") ?? "host";
            var device = Str(item, "device");
            var currentExec = Str(item, "currentExecId");
            var flat = new List<AgentSession>();
            foreach (var exec in execs.EnumerateArray())
            {
                if (exec.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var execId = Str(exec, "id") ?? NewId();
                flat.Add(new AgentSession
                {
                    Id = execId,
                    Name = Str(exec, "name") ?? parentName,
                    AutoNamed = !exec.TryGetProperty("autoNamed", out var an) || an.ValueKind != JsonValueKind.False,
                    Backend = backend == "server" ? "server" : "host",
                    Device = device,
                    PiSessionId = Str(exec, "piSession") ?? string.Empty,
                    PiOwned = !exec.TryGetProperty("piOwned", out var po) || po.ValueKind != JsonValueKind.False,
                    Turns = Int(exec, "turns"),
                    CreatedAt = Time(exec, "createdAt") ?? DateTimeOffset.Now,
                    UpdatedAt = Time(exec, "updatedAt") ?? DateTimeOffset.Now,
                    History = ReadHistory(exec),
                    Runs = ReadRuns(exec)
                });
            }

            if (flat.Count == 0)
            {
                return null;
            }

            // 老的“当前”语义：只保留那个；其他的也留着（用户要的就是能切）
            var current = flat.FirstOrDefault(f => f.Id == currentExec) ?? flat[0];
            current.UpdatedAt = DateTimeOffset.Now;
            return current;
        }

        return new AgentSession
        {
            Id = id,
            Name = Str(item, "name") ?? "会话",
            AutoNamed = !item.TryGetProperty("autoNamed", out var an2) || an2.ValueKind != JsonValueKind.False,
            Backend = (Str(item, "backend") ?? "host") == "server" ? "server" : "host",
            Device = Str(item, "device"),
            PiSessionId = Str(item, "piSession") ?? string.Empty,
            PiOwned = !item.TryGetProperty("piOwned", out var po2) || po2.ValueKind != JsonValueKind.False,
            Turns = Int(item, "turns"),
            CreatedAt = Time(item, "createdAt") ?? DateTimeOffset.Now,
            UpdatedAt = Time(item, "updatedAt") ?? DateTimeOffset.Now,
            History = ReadHistory(item),
            Runs = ReadRuns(item)
        };
    }

    private static List<HistoryEntry> ReadHistory(JsonElement obj)
    {
        var list = new List<HistoryEntry>();
        if (obj.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in history.EnumerateArray())
            {
                if (h.ValueKind == JsonValueKind.Object)
                {
                    list.Add(new HistoryEntry { Role = Str(h, "role") ?? "user", Text = Str(h, "text") ?? string.Empty });
                }
            }
        }

        return list;
    }

    private static List<SessionRun> ReadRuns(JsonElement obj)
    {
        var list = new List<SessionRun>();
        if (obj.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in runs.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                list.Add(new SessionRun
                {
                    Id = Str(r, "id") ?? NewId(),
                    Prompt = Str(r, "prompt") ?? string.Empty,
                    Result = Str(r, "result") ?? string.Empty,
                    Device = Str(r, "device"),
                    PiSession = Str(r, "piSession"),
                    Ok = r.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? ok.GetBoolean()
                        : null,
                    DurationMs = r.TryGetProperty("durationMs", out var dm) && dm.TryGetInt64(out var dmv) ? dmv : 0,
                    ToolCalls = r.TryGetProperty("toolCalls", out var tc) && tc.TryGetInt32(out var tcv) ? tcv : 0,
                    At = Time(r, "at") ?? DateTimeOffset.Now
                });
            }
        }

        return list;
    }

    private void Save()
    {
        try
        {
            var chats = new JsonObject();
            foreach (var (key, chat) in _chats)
            {
                var current = new JsonObject();
                foreach (var (backend, id) in chat.Current)
                {
                    current[backend] = id;
                }

                var sessions = new JsonArray();
                foreach (var s in chat.Sessions)
                {
                    var history = new JsonArray();
                    foreach (var h in s.History)
                    {
                        history.Add(new JsonObject { ["role"] = h.Role, ["text"] = h.Text });
                    }

                    var runs = new JsonArray();
                    foreach (var r in s.Runs)
                    {
                        runs.Add(new JsonObject
                        {
                            ["id"] = r.Id,
                            ["at"] = r.At.ToString("O"),
                            ["prompt"] = r.Prompt,
                            ["ok"] = r.Ok,
                            ["durationMs"] = r.DurationMs,
                            ["toolCalls"] = r.ToolCalls,
                            ["result"] = r.Result,
                            ["device"] = r.Device,
                            ["piSession"] = r.PiSession
                        });
                    }

                    sessions.Add(new JsonObject
                    {
                        ["id"] = s.Id,
                        ["name"] = s.Name,
                        ["autoNamed"] = s.AutoNamed,
                        ["backend"] = s.Backend,
                        ["device"] = s.Device,
                        ["piSession"] = s.PiSessionId,
                        ["piOwned"] = s.PiOwned,
                        ["turns"] = s.Turns,
                        ["createdAt"] = s.CreatedAt.ToString("O"),
                        ["updatedAt"] = s.UpdatedAt.ToString("O"),
                        ["history"] = history,
                        ["runs"] = runs
                    });
                }

                chats[key] = new JsonObject { ["current"] = current, ["sessions"] = sessions };
            }

            var payload = new JsonObject { ["chats"] = chats };
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(_path, payload.ToJsonString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log($"执行会话文件写失败：{ex.Message}");
        }
    }

    private static string? Str(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.TryGetInt32(out var i) ? i : 0;

    private static DateTimeOffset? Time(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
           DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed
            : null;
}
