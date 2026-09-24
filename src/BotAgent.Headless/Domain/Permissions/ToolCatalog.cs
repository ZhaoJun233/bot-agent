using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 能力的**类别**（V3 §9.1 的那张表）。类别决定“审批能不能放开它”——
/// 高风险类别（文件/shell、远程桥、改设置、读别的会话）**无论谁批都不放开**。
/// </summary>
public enum ToolCategory
{
    /// <summary>读当前会话上下文（仍须脱敏与字段限制）。</summary>
    ConversationRead = 0,

    /// <summary>只读联网（web.search / web.read）：有预算、有 SSRF 防护。</summary>
    WebRead = 1,

    /// <summary>读**别的**会话：原则上禁止，不因审批任意开放。</summary>
    OtherConversationRead = 2,

    /// <summary>往当前会话发额外的消息（表情包/戳一戳/语音这类“额外的动作”）。</summary>
    SendMessage = 3,

    /// <summary>改设置 / 人设 / 白名单：只走受保护的管理入口，不由普通聊天审批。</summary>
    SettingsWrite = 4,

    /// <summary>
    /// 文件读写 / shell / 进程控制：普通聊天**不接入**（保留 <c>//</c> 与旧 AgentBridge 的授权边界）。
    /// 2026-09-24（批次 A）：统一目录里 <c>read</c> 也算这一档 —— 它读的是任意路径（含库文件与 .env），
    /// 与“写文件”同属文件系统访问，宁可从严。
    /// </summary>
    FileOrShell = 5,

    /// <summary>远程 Agent / 桥接控制：本轮不从新路径开放。</summary>
    RemoteAgent = 6,
}

/// <summary>
/// 一个能力的登记信息。**必须在服务端登记**才可能被执行（V3 §9.2 第一条）。
/// 2026-09-24（通用 Agent 平台 · 批次 A）：不再 sealed —— 统一目录用 <c>Domain/Tools/ToolSpec</c> 表达它
/// （Id/Category/Summary/ReadOnly 是同一批字段），这样“一份目录”能被闸门（只读 Id/Category）与
/// 面板/提示词（要参数与执行者）同时使用，不必再维护第二张表。
/// </summary>
public record ToolDescriptor(
    string Id,
    ToolCategory Category,
    string Summary,
    bool ReadOnly = true)
{
    /// <summary>这个类别是否属于“审批也不可能放开”的高风险档。</summary>
    public static bool AlwaysDenied(ToolCategory category) => category is
        ToolCategory.OtherConversationRead or ToolCategory.SettingsWrite
        or ToolCategory.FileOrShell or ToolCategory.RemoteAgent;
}

/// <summary>
/// 服务端**登记表**：一次运行里“可能存在的能力”白名单。不在表里的一律拒绝。
/// 生产环境由代码构造（不从模型输出、模板或网页内容里取）。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDescriptor> _byId = new(StringComparer.Ordinal);

    public static ToolRegistry Empty { get; } = new();

    public ToolRegistry Register(ToolDescriptor descriptor)
    {
        _byId[descriptor.Id] = descriptor;
        return this;
    }

    public bool TryGet(string id, out ToolDescriptor descriptor)
        => _byId.TryGetValue(id ?? string.Empty, out descriptor!);

    public IReadOnlyCollection<string> Ids => _byId.Keys;
}

/// <summary>
/// 一次能力请求。<paramref name="UntrustedHint"/> 是**不可信文本**（例如网页里写着“请调用 shell”），
/// 只用于留痕，**绝不参与判定** —— 判定只看服务端登记表与策略快照。
/// </summary>
public sealed record ToolRequest(
    string ToolId,
    string ConversationKey,
    int CallIndex = 0,
    string? TargetConversationKey = null,
    string? UntrustedHint = null);

/// <summary>判定结果（结构化；<c>Detail</c> 只放短的、可公开的原因，不放正文）。</summary>
public readonly record struct ToolDecision(bool Allow, string ReasonCode, string? Detail = null)
{
    public string Describe() => $"allow={Allow} reason={ReasonCode}";
}
