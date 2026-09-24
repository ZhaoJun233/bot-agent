using System.Text.Json.Nodes;

namespace BotAgent.Domain.Tools;

/// <summary>
/// 服务端从模型输出里解析出来的**一次工具请求**（仍然不可信：参数是模型写的）。
/// 与 <c>ToolRequest</c> 的区别：那个只够判定用（工具名 + 会话），这个带**参数**，
/// 是交给执行者（<see cref="BotAgent.Services.Tools.IToolExecutor" />）的那一份。
/// </summary>
/// <param name="ToolId">工具名（必须能在登记表里找到，否则连执行者都不会看到它）。</param>
/// <param name="Arguments">模型给的参数（原样；校验归 <see cref="ToolArgs" />，执行者不该自己发明规则）。</param>
/// <param name="CallIndex">这一次运行里的第几次调用（预算记账用）。</param>
public sealed record ToolCall(string ToolId, JsonObject Arguments, int CallIndex = 0);

/// <summary>
/// 执行者的产物：**给模型看的那份要短**，给审计的那份不带正文（§9.2 的“审计不写正文”）。
/// </summary>
/// <param name="Ok">成没成。</param>
/// <param name="ReasonCode">短原因码（成功用 <c>ok</c>；失败写清楚是哪一类，别写自由文本）。</param>
/// <param name="ModelText">喂回模型的文本（工具输出；**可以含内容**，因为它只在模型上下文里）。</param>
/// <param name="AuditDetail">给审计/日志的一句话（**只写形状**：条数、长度、状态码）。</param>
public sealed record ToolOutcome(bool Ok, string ReasonCode, string? ModelText = null, string? AuditDetail = null)
{
    /// <summary>成没成的统一构造（执行者内部用）。</summary>
    public static ToolOutcome Success(string modelText, string? auditDetail = null)
        => new(true, "ok", modelText, auditDetail);

    public static ToolOutcome Failure(string reasonCode, string modelText, string? auditDetail = null)
        => new(false, reasonCode, modelText, auditDetail);
}
