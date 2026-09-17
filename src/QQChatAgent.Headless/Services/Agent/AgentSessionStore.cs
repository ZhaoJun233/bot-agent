using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// Agent 会话：让「//」任务能在**同一个话题里接着聊**，也能随时开新的、切回去、删掉。
///
/// 为什么要单独一层（而不是继续用一个固定的 pi session）：
///   • 号主 2026-09-17 要求：调用服务器内置 agent / 外部 agent 时能自由切换会话、新建、删除；
///   • 两个后端的“会话”含义不一样：
///       - 外部设备（pi）：会话就是 `--session-id`，pi 自己存历史（`~/.pi/agent/sessions/…`）；
///       - 服务器内置：没有外部进程记历史，得我们**自己存对话轮次**（存这个文件里）。
///
/// 存储：`<dataDir>/agent-sessions.json`，一台会话一条记录；上限见常量（防止无限长）。
/// 并发：所有变更走一把锁，落盘用同一个漏斗（写失败只记日志，绝不让会话功能把消息流程搞挂）。
/// </summary>
public sealed class AgentSessionStore
{
    /// <summary>一个会话最多留多少轮对话（服务器内置后端用；外部后端靠 pi 自己管）。</summary>
    private const int MaxTurns = 40;

    /// <summary>每个会话最多几个（超出时删掉最久没用的，且不动当前会话）。</summary>
    public const int MaxSessionsPerChat = 12;

    /// <summary>单个会话历史最多多少字（超了从头砍）。</summary>
    private const int MaxHistoryChars = 40000;

    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    /// <summary>内存镜像：chatKey → 该会话的会话表。</summary>
    private readonly Dictionary<string, ChatSessions> _chats = new(StringComparer.Ordinal);

    public AgentSessionStore(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>一个会话（对上层来说：名字 + 属于哪个后端 + 有哪几轮）。</summary>
    public sealed class AgentSession
    {
        public required string Id { get; init; }
        public string Name { get; set; } = "默认";

    /// <summary>名字是自动总结出来的（还能被任务第一句覆盖；手动改过就不动）。</summary>
    public bool AutoNamed { get; set; } = true;
        public string Backend { get; set; } = "host";     // host / server
        public string? Device { get; set; }              // 外部后端：具体是哪台设备（可空 = 当前在线的）
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
        public int Turns { get; set; }

        /// <summary>给 pi 用的会话 id（外部后端）。</summary>
        public string PiSessionId { get; set; } = string.Empty;

        /// <summary>服务器内置后端的对话历史（role: user/assistant）。</summary>
        public List<HistoryEntry> History { get; set; } = new();
    }

    public sealed class HistoryEntry
    {
        public string Role { get; set; } = "user";
        public string Text { get; set; } = string.Empty;
    }

    private sealed class ChatSessions
    {
        public Dictionary<string, string> Current { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AgentSession> Sessions { get; set; } = new();
    }

    // ══════════ 查询 ══════════

    /// <summary>某个会话（群/私聊）的全部 agent 会话（新的在前）。</summary>
    public List<AgentSession> List(string sourceKey)
    {
        lock (_gate)
        {
            return _chats.TryGetValue(sourceKey, out var chat)
                ? chat.Sessions.OrderByDescending(s => s.UpdatedAt).ToList()
                : new List<AgentSession>();
        }
    }

    /// <summary>当前会话（backend 指定时返回该后端那个；没有就建一个默认的）。</summary>
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
            chat.Current[backend] = created.Id;
            Save();
            return created;
        }
    }

    /// <summary>按 id 或名字找一个会话（名字不唯一时取最近用的那个）。</summary>
    public AgentSession? Find(string sourceKey, string idOrName)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(sourceKey, out var chat))
            {
                return null;
            }

            var key = (idOrName ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                return null;
            }

            return chat.Sessions.FirstOrDefault(s => string.Equals(s.Id, key, StringComparison.OrdinalIgnoreCase))
                   ?? chat.Sessions.Where(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
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

    // ══════════ 变更 ══════════

    /// <summary>新建一个会话并设为该后端的当前会话。</summary>
    public AgentSession Create(string sourceKey, string backend, string? name, string? device = null)
    {
        lock (_gate)
        {
            var chat = EnsureChat(sourceKey);
            var created = CreateLocked(sourceKey, chat, backend, name, device);
            chat.Current[backend] = created.Id;
            Save();
            return created;
        }
    }

    /// <summary>切换当前会话（按 id 或名字）。</summary>
    public bool Use(string sourceKey, string idOrName, out AgentSession? used)
    {
        lock (_gate)
        {
            used = Find(sourceKey, idOrName);
            if (used is null)
            {
                return false;
            }

            _chats[sourceKey].Current[used.Backend] = used.Id;
            used.UpdatedAt = DateTimeOffset.Now;
            Save();
            return true;
        }
    }

    /// <summary>删除会话（删掉的正好是当前会话时，自动补一个干净的默认会话）。</summary>
    public bool Delete(string sourceKey, string idOrName, out AgentSession? deleted)
    {
        lock (_gate)
        {
            var found = Find(sourceKey, idOrName);
            deleted = found;
            if (found is null || !_chats.TryGetValue(sourceKey, out var chat))
            {
                return false;
            }

            chat.Sessions.RemoveAll(s => s.Id == found.Id);
            if (chat.Current.TryGetValue(found.Backend, out var cur) && cur == found.Id)
            {
                chat.Current.Remove(found.Backend);
                var fresh = CreateLocked(sourceKey, chat, found.Backend,
                    found.Backend == "server" ? "默认（服务器）" : "默认", found.Device);
                chat.Current[found.Backend] = fresh.Id;
            }

            Save();
            return true;
        }
    }

    /// <summary>清空某个会话的历史（服务器后端的对话、外部后端的 pi session id 都换新的）。</summary>
    public bool Reset(string sourceKey, string idOrName)
    {
        lock (_gate)
        {
            var session = Find(sourceKey, idOrName);
            if (session is null)
            {
                return false;
            }

            session.History.Clear();
            session.Turns = 0;
            session.PiSessionId = NewPiSessionId(sourceKey, session.Id);
            session.UpdatedAt = DateTimeOffset.Now;
            Save();
            return true;
        }
    }

    /// <summary>服务器内置 agent 跑完一轮后：记下对话（给下一轮当上下文）。</summary>
    public void AppendTurn(string sourceKey, string sessionId, IReadOnlyList<(string Role, string Text)> messages)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(sourceKey, out var chat) ||
                chat.Sessions.FirstOrDefault(s => s.Id == sessionId) is not { } session)
            {
                return;
            }

            session.History.Clear();
            foreach (var (role, text) in messages)
            {
                if (text.Length == 0)
                {
                    continue;
                }

                session.History.Add(new HistoryEntry { Role = role, Text = text });
            }

            // 太长就从头砍（保留最近的）
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

    /// <summary>服务器内置 agent 的上下文（喂给模型的历史）。</summary>
    public List<(string Role, string Text)> History(string sourceKey, string sessionId)
    {
        lock (_gate)
        {
            return _chats.TryGetValue(sourceKey, out var chat) &&
                   chat.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } session
                ? session.History.Select(h => (h.Role, h.Text)).ToList()
                : new List<(string Role, string Text)>();
        }
    }

    /// <summary>外部后端跑任务时要用的 pi 会话 id（空的话现场生成一个）。</summary>
    public string PiSessionId(string sourceKey, string sessionId)
    {
        lock (_gate)
        {
            if (_chats.TryGetValue(sourceKey, out var chat) &&
                chat.Sessions.FirstOrDefault(s => s.Id == sessionId) is { } session)
            {
                if (session.PiSessionId.Length == 0)
                {
                    session.PiSessionId = NewPiSessionId(sourceKey, session.Id);
                    Save();
                }

                return session.PiSessionId;
            }

            return NewPiSessionId(sourceKey, sessionId);
        }
    }

    // ══════════ 内部 ══════════

    private static string NewPiSessionId(string sourceKey, string sessionId)
        => $"qqchat-{sourceKey.Replace(":", "-")}-{sessionId}";

    private ChatSessions EnsureChat(string sourceKey)
    {
        if (!_chats.TryGetValue(sourceKey, out var chat))
        {
            chat = new ChatSessions();
            _chats[sourceKey] = chat;
        }

        return chat;
    }

    /// <summary>
    /// 用任务的第一句话自动给会话起个标题（号主 2026-09-17：“没有简单对会话进行标题总结”）。
    /// 为什么不用模型总结：起个能认出来的名字不值得多一次模型调用 + 多几秒延迟；
    /// 把客套话剔掉、截到 16 个字，已经能在 //sessions 里一眼认出是哪个话题。
    /// </summary>
    public static string AutoTitle(string prompt)
    {
        var text = (prompt ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0)
        {
            return "新会话";
        }

        // 长前缀先剔（不然“帮我看看”会被“帮我”吃一半）
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

    /// <summary>第一次真的在这个会话里干活时，用那句提示词当标题（只覆盖自动名）。</summary>
    public void TitleFromPrompt(string sourceKey, string sessionId, string prompt)
    {
        lock (_gate)
        {
            if (!_chats.TryGetValue(sourceKey, out var chat) ||
                chat.Sessions.FirstOrDefault(s => s.Id == sessionId) is not { } session ||
                !session.AutoNamed)
            {
                return;
            }

            session.Name = AutoTitle(prompt);
            session.UpdatedAt = DateTimeOffset.Now;
            Save();
        }
    }

    /// <summary>手动改名（改过就不会再被自动标题覆盖）。</summary>
    public bool Rename(string sourceKey, string idOrName, string newName)
    {
        lock (_gate)
        {
            var session = Find(sourceKey, idOrName);
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

    private AgentSession CreateLocked(string sourceKey, ChatSessions chat, string backend, string? name, string? device)
    {
        // 清理：超出上限时删掉最久没动过的（当前会话不能删）
        if (chat.Sessions.Count >= MaxSessionsPerChat)
        {
            var keepIds = chat.Current.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var victim = chat.Sessions.Where(s => !keepIds.Contains(s.Id))
                .OrderBy(s => s.UpdatedAt)
                .FirstOrDefault();
            if (victim is not null)
            {
                chat.Sessions.Remove(victim);
            }
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var auto = name is { Length: > 0 } ? name.Trim() : $"会话{chat.Sessions.Count + 1}";
        var session = new AgentSession
        {
            Id = id,
            Name = auto.Length > 20 ? auto[..20] : auto,
            Backend = backend,
            Device = device,
            PiSessionId = NewPiSessionId(sourceKey, id)
        };

        session.AutoNamed = name is not { Length: > 0 };   // 手动起的名字不覆盖
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
                var key = entry.Name;
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
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var id = item.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.String
                            ? idNode.GetString()!
                            : null;
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            continue;
                        }

                        var session = new AgentSession
                        {
                            Id = id,
                            Backend = Str(item, "backend") ?? "host",
                            Device = Str(item, "device"),
                            Name = Str(item, "name") ?? "会话",
                            AutoNamed = !item.TryGetProperty("autoNamed", out var an) || an.ValueKind != JsonValueKind.False,
                            PiSessionId = Str(item, "piSession") ?? string.Empty,
                            Turns = item.TryGetProperty("turns", out var t) && t.TryGetInt32(out var tv) ? tv : 0
                        };
                        session.CreatedAt = Time(item, "createdAt") ?? DateTimeOffset.Now;
                        session.UpdatedAt = Time(item, "updatedAt") ?? session.CreatedAt;

                        if (item.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var h in history.EnumerateArray())
                            {
                                if (h.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                session.History.Add(new HistoryEntry
                                {
                                    Role = Str(h, "role") ?? "user",
                                    Text = Str(h, "text") ?? string.Empty
                                });
                            }
                        }

                        chat.Sessions.Add(session);
                    }
                }

                _chats[key] = chat;
            }

            _log($"会话记录已恢复：{_chats.Count} 个聊天 / {_chats.Sum(c => c.Value.Sessions.Count)} 个 agent 会话");
        }
        catch (Exception ex)
        {
            // 文件坏了就当没有（会话丢了可以再建，不能因此起不来）
            _log($"agent 会话文件读不了，按空的继续：{ex.Message}");
        }
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
                foreach (var session in chat.Sessions)
                {
                    var history = new JsonArray();
                    foreach (var entry in session.History)
                    {
                        history.Add(new JsonObject { ["role"] = entry.Role, ["text"] = entry.Text });
                    }

                    sessions.Add(new JsonObject
                    {
                        ["id"] = session.Id,
                        ["name"] = session.Name,
                        ["autoNamed"] = session.AutoNamed,
                        ["backend"] = session.Backend,
                        ["device"] = session.Device,
                        ["piSession"] = session.PiSessionId,
                        ["turns"] = session.Turns,
                        ["createdAt"] = session.CreatedAt.ToString("O"),
                        ["updatedAt"] = session.UpdatedAt.ToString("O"),
                        ["history"] = history
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
            _log($"agent 会话文件写失败：{ex.Message}");
        }
    }

    private static string? Str(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement obj, string key)
        => obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
           DateTimeOffset.TryParse(v.GetString(), out var parsed)
            ? parsed
            : null;
}
