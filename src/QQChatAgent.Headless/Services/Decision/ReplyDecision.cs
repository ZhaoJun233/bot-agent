using System;
using System.Linq;

namespace QQChatAgent.Services.Decision;

/// <summary>
/// 模型这一轮**打算做什么**（V3 §8.1 的 <c>action</c>）。
/// 取值由服务端定义，模型只能填名字；填了不认识的名字按<b>静默</b>处理（Fail-Closed）。
/// </summary>
public enum ReplyAction
{
    /// <summary>正常回复：走既有的目标校验 / 文本清洗 / 长度限制 / 发送。</summary>
    Reply = 0,

    /// <summary>显式静默：服务端不发任何 QQ 消息。</summary>
    Silent = 1,

    /// <summary>要在群里提问 / 求确认（真正落地走 P3 的审批状态，本轮还没有接入）。</summary>
    Ask = 2,

    /// <summary>想调用工具（普通群聊路径没有工具，必须经过 P3 的权限与审批）。</summary>
    Tool = 3,
}

/// <summary>
/// 一次「这一轮到底发不发」的**服务端结论**（结构化，不含正文落盘）。
/// </summary>
/// <param name="Action">模型建议的动作（已归一化；不认识的动作归一到 <see cref="ReplyAction.Silent"/>）。</param>
/// <param name="Send">服务端最终结论：**只有它为 true 才允许把 <paramref name="Content"/> 发到 QQ**。</param>
/// <param name="ReasonCode">结构化原因码（短、无正文、可写进运行记录）。</param>
/// <param name="Content">允许发送的正文；<see cref="Send"/> 为 false 时**必为 null**（静默标记绝不进发送队列）。</param>
/// <param name="Malformed">模型输出不合法（非法动作 / 空回复 / 上游空响应）—— 用于区分「它选择不说」与「它说错了」。</param>
public readonly record struct DecisionVerdict(
    ReplyAction Action,
    bool Send,
    string ReasonCode,
    string? Content,
    bool Malformed = false,

    /// <summary>
    /// 模型想调的工具名（已按短标识符形状清洗；只用于**记录与展示**，
    /// **不构成授权** —— 服务端只认自己固定的那个工具，见 <c>ApprovalFlow.FixedToolId</c>）。
    /// </summary>
    string? ToolId = null,

    /// <summary>
    /// 模型想问的问题（仅当 <c>action=ask</c> 且面板「允许提问」打开时才有值）。
    /// **它不是正文**：<see cref="Content"/> 仍然是 null（不变量不变），
    /// 服务端会把它包在**自己的**文案里（带编号与有效期）再发出去。
    /// </summary>
    string? QuestionText = null)
{
    /// <summary>只给日志用的单行摘要（**不含正文**）。</summary>
    public string Describe()
        => $"action={Action} send={Send} reason={ReasonCode}"
            + (ToolId is { Length: > 0 } tool ? $" tool={tool}" : string.Empty)
            + (Malformed ? " malformed=true" : string.Empty);
}

/// <summary>
/// 「这一轮发不发」的判定规则（V3 §8.2 / §8.3）。
///
/// 为什么单独一个纯类：判定必须能用合成输入**确定性地**测（V3 §8.4），
/// 不能藏在 <c>BotAgent</c> 的大流程里 —— 那里没法只测判定、也没法保证测试不碰真实链路。
///
/// 三条硬口径：
///   1. **模型只是候选建议**：模型给的动作经过这里的白名单归一，非法动作一律降级为静默；
///   2. **静默一定不发**：<see cref="Send"/> 为 false 时 <see cref="DecisionVerdict.Content"/> 必为 null，
///      调用方即使不看 <c>Send</c> 也拿不到要发的东西（控制标记进不了发送队列）；
///   3. **不确定时降级，不升级**：解析失败、字段超长、动作不认识 → 静默，不按“普通文本”放行。
/// </summary>
public static class ReplyDecisionRules
{
    /// <summary>兼容用的显式静默标记（旧协议：模型直接回这一串）。</summary>
    public const string SilentMarker = "[SILENT]";

    /// <summary>结构化控制字段（原因码/动作名）允许的最大长度；超长视为不可信，降级处理。</summary>
    public const int MaxControlFieldLength = 64;

    /// <summary>动作名 → <see cref="ReplyAction"/>；不认识返回 null（由调用方降级为静默）。</summary>
    public static ReplyAction? ParseAction(string? raw)
    {
        var token = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return token switch
        {
            "reply" or "respond" or "say" => ReplyAction.Reply,
            "silent" or "silence" or "skip" => ReplyAction.Silent,
            "ask" or "question" => ReplyAction.Ask,
            "tool" or "call" => ReplyAction.Tool,
            _ => null,
        };
    }

    /// <summary>
    /// 纯文本是不是**恰好**一个静默标记（去首尾空白后完全相等）。
    /// 大小写不敏感（<c>[SILENT]</c> / <c>[silent]</c>），但**不做**无限放宽：
    /// 正文里夹着标记（"他说 [SILENT] 是什么意思"）不算静默 —— 那是内容，不是控制。
    /// </summary>
    public static bool IsExactSilentMarker(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length > 0 &&
               trimmed.Equals(SilentMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>正文里**出现**了静默标记（用于记一条告警：控制串混进了内容）。</summary>
    public static bool ContainsSilentMarker(string? text)
        => (text ?? string.Empty).Contains(SilentMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 原因码清洗：只留短标识符（字母/数字/下划线/连字符/点），超长或含异常字符的用兜底值。
    /// 目的：运行记录里的 reasonCode 永远是自己人写的短串，不会是模型塞进来的长文本。
    /// </summary>
    public static string SanitizeReasonCode(string? raw, string fallback)
    {
        var token = (raw ?? string.Empty).Trim();
        if (token.Length == 0 || token.Length > MaxControlFieldLength)
        {
            return fallback;
        }

        var ok = token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':');
        return ok ? token : fallback;
    }

    /// <summary>
    /// 工具名清洗：只接受短标识符形状（字母/数字/下划线/连字符/点），其余一律丢弃。
    /// 清洗结果**只是记录用**：服务端绝不因为它就真的去调那个工具。
    /// </summary>
    public static string? SanitizeToolId(string? raw)
    {
        var token = (raw ?? string.Empty).Trim();
        if (token.Length == 0 || token.Length > MaxControlFieldLength)
        {
            return null;
        }

        var ok = token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
        return ok ? token : null;
    }

    /// <summary>提问文本的长度上限（超了截断；它会被服务端包进自己的文案里发出去）。</summary>
    public const int MaxQuestionLength = 200;

    /// <summary>
    /// 提问文本清洗：**压成一行**（换行/制表/控制字符 → 空格）、去掉首尾空白、超长截断。
    ///
    /// 为什么必须压成一行：它会和编号、有效期拼在同一条消息里发给群友 ——
    /// 带换行的文本能把后半句（“120 秒内有效”这类）挤到看不见的地方，看起来像服务器没说清楚。
    /// 截断则保证一条提问不至于把群聊刷屏。
    /// </summary>
    public static string? SanitizeQuestion(string? raw)
    {
        var text = (raw ?? string.Empty);
        if (text.Length == 0)
        {
            return null;
        }

        var chars = text.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        var line = new string(chars).Trim();
        while (line.Contains("  ", StringComparison.Ordinal))
        {
            line = line.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (line.Length == 0)
        {
            return null;
        }

        return line.Length <= MaxQuestionLength ? line : line[..MaxQuestionLength] + "…";
    }

    /// <summary>
    /// 最终判定：结合模型的 <paramref name="action"/>（可空 = 旧协议）与 <paramref name="reply"/> 正文，
    /// 给出「发不发 / 发什么 / 为什么」。
    /// </summary>
    /// <param name="action">模型写的 action 原样（未归一）；null/空 = 旧协议，没有这个字段。</param>
    /// <param name="reply">模型写的 reply 正文（已 Trim）；null/空白 = 没写。</param>
    /// <param name="reasonCode">模型附带的原因码（可选，仅用于记录，不参与权限判断）。</param>
    /// <param name="upstreamEmpty">上游回了 200 但没给 content（网关吞回复）—— 与“模型选择沉默”要分开记。</param>
    public static DecisionVerdict Decide(
        string? action,
        string? reply,
        string? reasonCode = null,
        bool upstreamEmpty = false,
        string? toolRequest = null,
        bool askEnabled = false)
    {
        var body = (reply ?? string.Empty).Trim();

        if (upstreamEmpty)
        {
            // 上游没给内容：不发，但标记 malformed（号主要能看出这是网关出事了，不是模型不想说）
            return new DecisionVerdict(ReplyAction.Silent, false, "upstream_empty", null, Malformed: true);
        }

        var declared = (action ?? string.Empty).Trim();
        if (declared.Length == 0)
        {
            // ── 旧协议：没有 action 字段，按 reply 正文判断 ──
            if (body.Length == 0)
            {
                return new DecisionVerdict(ReplyAction.Silent, false, "empty_reply", null, Malformed: true);
            }

            if (IsExactSilentMarker(body))
            {
                // 兼容标记：静默，且正文**绝不**外发（不是把 "[SILENT]" 发出去）
                return new DecisionVerdict(ReplyAction.Silent, false, "silent_marker", null);
            }

            return new DecisionVerdict(ReplyAction.Reply, true, "legacy_text", body);
        }

        // ── 新协议：有 action 字段 ──
        if (declared.Length > MaxControlFieldLength)
        {
            // 超长控制字段不可信：降级为静默（V3 §8.2「超长控制字段不能默认发送」）
            return new DecisionVerdict(ReplyAction.Silent, false, "action_too_long", null, Malformed: true);
        }

        var parsed = ParseAction(declared);
        if (parsed is null)
        {
            // 不认识的动作：降级为静默（**不**按 reply 正文放行 —— 未知控制字段说明协议已错位）
            return new DecisionVerdict(ReplyAction.Silent, false, "unknown_action", null, Malformed: true);
        }

        switch (parsed.Value)
        {
            case ReplyAction.Silent:
                return new DecisionVerdict(
                    ReplyAction.Silent, false, SanitizeReasonCode(reasonCode, "model_silent"), null);

            case ReplyAction.Ask:
                // 提问/审批要走 P3 的待答状态；在接入之前**不发**（Fail-Closed），
                // 但也不当成格式错误 —— 这是合法动作，只是服务端还没打开这条能力。
                // 打开之后（面板「允许提问」）：正文**仍然不发**（它是提问，不是回复），
                // 只把问题文本带出来，由服务端包一层自己的文案 + 编号 + 有效期再发。
                return askEnabled
                    ? new DecisionVerdict(
                        ReplyAction.Ask, false, "ask_pending", null, QuestionText: SanitizeQuestion(body))
                    : new DecisionVerdict(ReplyAction.Ask, false, "ask_not_enabled", null);

            case ReplyAction.Tool:
                // 普通群聊路径没有工具权限（V3 §9.1）：静默，等 P3 的策略与审批生效
                return new DecisionVerdict(
                    ReplyAction.Tool, false, "tool_not_enabled", null, ToolId: SanitizeToolId(toolRequest));

            default:
                if (body.Length == 0)
                {
                    return new DecisionVerdict(ReplyAction.Reply, false, "empty_reply", null, Malformed: true);
                }

                if (IsExactSilentMarker(body))
                {
                    // action=reply 但正文只有标记：仍然静默（标记不许发出去）
                    return new DecisionVerdict(ReplyAction.Silent, false, "silent_marker", null);
                }

                return new DecisionVerdict(
                    ReplyAction.Reply, true, SanitizeReasonCode(reasonCode, "replied"), body);
        }
    }
}
