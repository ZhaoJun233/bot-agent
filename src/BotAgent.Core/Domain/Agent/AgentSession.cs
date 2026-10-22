namespace BotAgent.Domain.Agent;

/// <summary>
/// 一个执行会话（<c>//</c> 任务用的上下文）。纯数据 —— 存储、排序与落盘在适配层。
///
/// ⚠ 时间默认值是 <c>default</c> 而**不是** <c>Clock.Now</c>：Domain 不许读时钟（R1 钉着，
/// 纯规则靠显式 now 参数）。库/调用方在真正建会话时显式写 <c>CreatedAt</c> / <c>UpdatedAt</c>
/// （见 <c>AgentSessionStore.CreateLocked</c>），从 JSON 读回来时也由反序列化覆盖。
/// </summary>
public sealed class AgentSession
{
    public required string Id { get; init; }
    public string Name { get; set; } = "会话1";

    /// <summary>名字是自动综结的（agent 每跑完一轮按上下文重新综结；手动改过就不动）。</summary>
    public bool AutoNamed { get; set; } = true;

    /// <summary>host = 外部设备上的 pi；server = 服务器内置工具循环。</summary>
    public string Backend { get; set; } = "host";

    /// <summary>外部后端：具体哪台设备（空 = 当前在线的第一台）。</summary>
    public string? Device { get; set; }

    /// <summary>外部后端：pi 那边的 session-id。</summary>
    public string PiSessionId { get; set; } = string.Empty;

    /// <summary>这个 pi 会话是我们建的吗（false = 从 pi 里导入的，删的时候要小心）。</summary>
    public bool PiOwned { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>服务器内置：这段上下文的轮数。</summary>
    public int Turns { get; set; }

    /// <summary>服务器内置：对话历史（外部后端由 pi 自己存）。</summary>
    public List<HistoryEntry> History { get; set; } = new();

    /// <summary>这个会话里每次任务的流水（小会话级的“执行记录”）。</summary>
    public List<SessionRun> Runs { get; set; } = new();
}

/// <summary>会话里的一轮对话（agent 内置后端自己维护的那份上下文）。</summary>
public sealed class HistoryEntry
{
    public string Role { get; set; } = "user";
    public string Text { get; set; } = string.Empty;
}

/// <summary>一次任务执行流水。</summary>
public sealed class SessionRun
{
    public required string Id { get; init; }
    public DateTimeOffset At { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public bool? Ok { get; set; }                    // null = 还在跑
    public long DurationMs { get; set; }
    public int ToolCalls { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? Device { get; set; }
    public string? PiSession { get; set; }
}
