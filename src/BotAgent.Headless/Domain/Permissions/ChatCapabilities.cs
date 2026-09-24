using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 普通群聊这一路**实际能力**的服务端集合（V3 §9.1 / §9.3）。
///
/// 为什么要有它：聊天回复本来就带着几个“动作”（联网查、发语音、表情包、戳一戳、点歌），
/// 以前这些开关散落在 <c>GenerateReplyAsync</c> 的各个 <c>if</c> 里。这里把它们收成
/// **一份服务端策略快照 + 一张登记表**，判定统一走 <see cref="ToolGate" />，
/// 于是“模型/网页/模板能不能扩大权限”这件事变成可测的（而不是靠读代码数 if）。
///
/// 兼容红线（V3 §5.3）：**没配场景时与改造前逐字一致** ——
/// 白名单直接由现有开关拼出来，预算无限、不需要审批。
/// 只有在面板里显式填了场景名，预设白名单才生效；而且预设只能**收紧**，不能把面板关掉的能力打开。
/// </summary>
public sealed class ChatCapabilitySet
{
    /// <summary>聊天这一路允许登记的能力（高风险类别**不登记**，于是连“未知工具”都算不上）。</summary>
    public static readonly ToolRegistry DefaultRegistry = new ToolRegistry()
        .Register(new ToolDescriptor("chat.reply", ToolCategory.ConversationRead, "回复当前会话"))
        .Register(new ToolDescriptor("web.search", ToolCategory.WebRead, "联网搜索"))
        .Register(new ToolDescriptor("web.read", ToolCategory.WebRead, "读网页正文"))
        .Register(new ToolDescriptor("music.listen", ToolCategory.WebRead, "去听一首歌（只读外部数据）"))
        .Register(new ToolDescriptor("music.share", ToolCategory.SendMessage, "分享歌曲卡片"))
        .Register(new ToolDescriptor("voice.speak", ToolCategory.SendMessage, "用语音说一句"))
        .Register(new ToolDescriptor("sticker.send", ToolCategory.SendMessage, "发表情包"))
        .Register(new ToolDescriptor("poke.send", ToolCategory.SendMessage, "戳一戳"))
        .Register(new ToolDescriptor(
            ApprovalFlow.FixedToolId,
            ToolCategory.SendMessage,
            ApprovalFlow.FixedToolSummary,
            ReadOnly: false))
        .Register(new ToolDescriptor(
            ApprovalFlow.QuestionToolId,
            ToolCategory.SendMessage,
            "往当前会话问一个问题（带编号与有效期；不授予任何权限）",
            ReadOnly: false));

    public ToolRegistry Registry { get; }

    public ToolPolicy Policy { get; }

    private ChatCapabilitySet(ToolRegistry registry, ToolPolicy policy)
    {
        Registry = registry;
        Policy = policy;
    }

    /// <summary>场景名（空 = 跟随现有开关，即改造前的行为）。</summary>
    public string Scenario { get; private init; } = string.Empty;

    /// <summary>本集合的“一票否决”集合：这次没开的能力，任何模板/模型输出都开不出来。</summary>
    public static ChatCapabilitySet FromSwitches(
        bool enableWebSearch,
        bool enableMusic,
        bool enableVoice,
        bool enableStickers,
        bool enablePoke,
        string? scenario,
        int policyVersion = 1,
        bool approvalsEnabled = false,
        bool questionsEnabled = false)
    {
        bool SwitchAllows(string id) => id switch
        {
            "chat.reply" => true,
            "web.search" or "web.read" => enableWebSearch,
            "music.listen" or "music.share" => enableMusic,
            "voice.speak" => enableVoice,
            "sticker.send" => enableStickers,
            "poke.send" => enablePoke,
            _ => false,   // 不认识的能力名：一律不开（Fail-Closed）
        };

        var token = (scenario ?? string.Empty).Trim().ToLowerInvariant();
        if (token.Length == 0)
        {
            var allowed = DefaultRegistry.Ids.Where(SwitchAllows).ToHashSet(StringComparer.Ordinal);
            if (approvalsEnabled)
            {
                // 开了审批：把**唯一的固定假工具**放进白名单，并且**只有它**必须批过才执行。
                // 其余能力照旧（voice/sticker/poke 不因打开审批而多一道门槛）—— 这条是刻意的：
                // 审批开关默认关，打开它应该只是“多一条可演示的审批闭环”，而不是改写既有能力。
                allowed.Add(ApprovalFlow.FixedToolId);
            }

            if (questionsEnabled)
            {
                // 允许提问：把「提问」这一个动作放进白名单。它**不需要审批**（不是执行动作），
                // 但仍然要过闸门 —— “往当前会话发额外消息”这件事走的是同一条判定路径。
                allowed.Add(ApprovalFlow.QuestionToolId);
            }

            return new ChatCapabilitySet(
                DefaultRegistry,
                new ToolPolicy(
                    allowed,
                    MaxCallsPerRun: int.MaxValue,   // 沿用旧行为：一轮里可以同时触发多个动作
                    ApprovableCategories: approvalsEnabled
                        ? new HashSet<ToolCategory> { ToolCategory.SendMessage }
                        : new HashSet<ToolCategory>(),
                    PolicyVersion: policyVersion,
                    // **显式空集** = 明确“这个类别不需要审批” → 与改造前逐字一致（V3 §5.3 兼容红线）。
                    // 不写这个字段的话会落到 ToolPolicy 的默认口径（SendMessage 要审批），那就会改变既有行为。
                    ApprovalRequiredCategories: new HashSet<ToolCategory>(),
                    ApprovalRequiredTools: approvalsEnabled
                        ? new HashSet<string>(StringComparer.Ordinal) { ApprovalFlow.FixedToolId }
                        : null))
            {
                Scenario = string.Empty,
            };
        }

        // 配了场景：预设白名单 ∩ 面板开关（预设不能把面板关掉的能力打开）
        var preset = ScenarioPresets.Resolve(token, policyVersion);
        var narrowed = preset.AllowedTools.Where(SwitchAllows).ToHashSet(StringComparer.Ordinal);

        // 审批要求**只按工具名**表达：预设里显式写空集，不然会落到 ToolPolicy 的默认口径
        // （“SendMessage 一律要审批”）—— 那样 ask.question 永远开不出票、social 里的
        // 语音/表情/戳也全部变成不可批准的 approval_required，与预设注释里
        // “审批只覆盖点名的固定假工具”的意图矛盾（V3 §9.3 / §9.4）。
        preset = preset with { ApprovalRequiredCategories = new HashSet<ToolCategory>() };

        if (approvalsEnabled)
        {
            narrowed.Add(ApprovalFlow.FixedToolId);
            preset = preset with
            {
                ApprovalRequiredTools = new HashSet<string>(StringComparer.Ordinal) { ApprovalFlow.FixedToolId },
                // 批准要能真的放开它：固定假工具属 SendMessage，而 on-demand 的
                // ApprovableCategories 是空集、research 只有 WebRead —— 不并进去就会出现
                // “批过了仍然 approval_cannot_grant”，演示闭环在两种预设下走不通。
                ApprovableCategories = new HashSet<ToolCategory>(
                    preset.ApprovableCategories ?? new HashSet<ToolCategory>())
                {
                    ToolCategory.SendMessage,
                },
            };
        }

        if (questionsEnabled)
        {
            narrowed.Add(ApprovalFlow.QuestionToolId);
        }

        return new ChatCapabilitySet(DefaultRegistry, preset with { AllowedTools = narrowed })
        {
            Scenario = token,
        };
    }

    /// <summary>判定一次能力请求（登记表 + 策略快照 + 可选票据）。</summary>
    public ToolDecision Check(
        string toolId,
        string conversationKey,
        int callIndex = 0,
        string? targetConversationKey = null,
        string? untrustedHint = null,
        ApprovalTicket? ticket = null)
        => ToolGate.Evaluate(
            Registry,
            Policy,
            new ToolRequest(toolId, conversationKey, callIndex, targetConversationKey, untrustedHint),
            ticket);

    /// <summary>只给日志/面板看的单行摘要（能力名，不含正文）。</summary>
    public string Describe()
        => $"scene={(Scenario.Length == 0 ? "(跟随开关)" : Scenario)}"
            + $" caps=[{string.Join(",", Policy.AllowedTools.OrderBy(x => x, StringComparer.Ordinal))}]"
            + $" budget={Policy.MaxCallsPerRun} v{Policy.PolicyVersion}"
            + (Policy.ApprovalRequiredTools is { Count: > 0 } need
                ? $" needApproval=[{string.Join(",", need.OrderBy(x => x, StringComparer.Ordinal))}]"
                : string.Empty);

    /// <summary>
    /// 策略**指纹**（登记表 + 策略内容）。用途：`BotAgentHost` 只在指纹真的变了时才换策略版本 ——
    /// 面板上保存一次无关键（比如改了个阈值）不该把在途审批单弄失效。
    /// </summary>
    public string PolicyFingerprint
        => "reg[" + string.Join(",", Registry.Ids.OrderBy(x => x, StringComparer.Ordinal)) + "] " + Policy.Fingerprint();
}
