using System.Text.Json.Serialization;
using BotAgent.Domain.Agent;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Agent;

/// <summary>
/// 一次 agent 任务（服务器内置后端共用；host 字段保留只读兼容，运行时不填）。
/// 从退役的本机桥文件中抽出，供服务器路径继续使用。
/// </summary>
public sealed class AgentTask
{
    public required string Id { get; init; }

    /// <summary>哪个会话提的（群聊 group:123 / 私聊 private:456）。</summary>
    public required string SourceKey { get; init; }

    [JsonIgnore]
    public PlatformSourceLease? SourceLease { get; internal set; }

    public required string Prompt { get; init; }

    /// <summary>
    /// 附加提示词（面板里的「Agent 附加提示词」，默认 = 隐私红线）：
    /// 服务器内置 agent 拼进它自己的 system prompt。
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>host 历史字段：这个任务派给哪台设备（服务器路径为空）。</summary>
    public string? TargetDevice { get; init; }

    /// <summary>host 历史字段：实际执行它的外部设备名（服务器路径为空）。</summary>
    internal string? ConnectionId { get; set; }

    public string? DeviceName { get; set; }

    /// <summary>会话标识（服务器任务目前为空；保留字段兼容旧数据）。</summary>
    public required string Session { get; init; }

    public string? WorkDir { get; set; }

    public string? Model { get; set; }

    public string? Tools { get; set; }

    public int TimeoutSeconds { get; set; }

    /// <summary>配置里那个模型名在这台设备上不存在时，记下原值（历史字段）。</summary>
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

    /// <summary>最近一次“我在干什么”（例如 🔧 bash）——群里报进度用。</summary>
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
