using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Platforms;
using BotAgent.Services.Tools;

namespace BotAgent.EngineProbe;

/// <summary>
/// #115 的可见性单入口：<see cref="ToolVisibility" /> 的六条行为。
///
/// 这些用例存在的理由很具体 —— 改造前“开关 → 工具可见”的换算散在 ChatCapabilities 与
/// ReplyToolAvailability 两处，改一个键要改两处，且平台策略里根本没有 approval/question
/// 这两个能力名（只有 text），曾经把审批/提问整条链静默关掉。断言把这两条都钉住。
///
/// 口径：纯合成 AppSettings + 纯合成策略快照；不读设置文件、不连网、不启动机器人。
/// </summary>
public static class ToolVisibilityTests
{
    public static Task SwitchesAndUnknownIdsAsync()
    {
        var settings = new AppSettings { EnableStickers = false, EnableWebSearch = false, EnableMusic = false, EnablePoke = false };
        var visibility = new ToolVisibility();

        Check(!visibility.Allows(settings, null, "no.such.tool"), "未登记的工具必须不可见（Fail-Closed）");
        Check(!visibility.Allows(settings, null, "sticker.send"), "开关关掉时贴纸工具不可见");
        Check(visibility.Allows(settings, null, "chat.reply"), "chat.reply 不挂开关，任何时候都可见");

        settings.EnableStickers = true;
        Check(visibility.Allows(settings, null, "sticker.send"), "开关打开后贴纸工具可见");
        return Task.CompletedTask;
    }

    public static Task WebReadyGateAsync()
    {
        var settings = new AppSettings { EnableWebSearch = true };
        var notReady = new ToolVisibility(webReady: () => false);
        var ready = new ToolVisibility(webReady: () => true);

        Check(!notReady.Allows(settings, null, "web.search"), "联网未就绪时搜索工具不可见（即使开关开着）");
        Check(ready.Allows(settings, null, "web.search"), "联网就绪且开关开着时搜索工具可见");
        return Task.CompletedTask;
    }

    public static Task StoppedLeaseRevokesEverythingAsync()
    {
        var settings = new AppSettings { EnableStickers = true };
        var stopped = new ToolVisibility(isDisposed: () => true);

        Check(!stopped.IsCurrent, "停机后租约立刻失效");
        Check(!stopped.Allows(settings, null, "chat.reply"), "停机后连 chat.reply 也不可见");
        Check(!stopped.Allows(settings, null, "sticker.send"), "停机后所有工具一起失效（不是逐个判断）");
        return Task.CompletedTask;
    }

    public static Task ApprovalAndQuestionCountAsTextAsync()
    {
        var settings = new AppSettings { EnableApprovals = true, EnableQuestions = true };
        var visibility = new ToolVisibility();
        var policy = Policy(PlatformCapabilities.QqOneBot);

        // 回归点：平台策略只认 text/image/voice/… 这套能力名；若直接把开关名当能力名去查，
        // Feature("approval") 会返回 supported=false，审批与提问从此整条链不可用。
        Check(visibility.Allows(settings, policy, ApprovalFlow.FixedToolId), "审批工具按 text 能力判定（不被平台策略误禁）");
        Check(visibility.Allows(settings, policy, ApprovalFlow.QuestionToolId), "提问工具按 text 能力判定（不被平台策略误禁）");

        settings.EnableApprovals = false;
        Check(!visibility.Allows(settings, policy, ApprovalFlow.FixedToolId), "开关关掉时审批工具不可见");
        return Task.CompletedTask;
    }

    public static Task PlatformPolicyCanCloseTextAsync()
    {
        var settings = new AppSettings { EnableApprovals = true };
        var visibility = new ToolVisibility();
        var closed = Policy(PlatformCapabilities.QqOneBot, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["text"] = false });

        Check(!visibility.Allows(settings, closed, "chat.reply"), "平台策略把 text 关掉后 chat.reply 不可见");
        Check(!visibility.Allows(settings, closed, ApprovalFlow.FixedToolId), "审批同属 text，一起被关掉");
        return Task.CompletedTask;
    }

    public static Task LiveResolverBeatsStaleSnapshotAsync()
    {
        var settings = new AppSettings();
        var open = Policy(PlatformCapabilities.QqOneBot);
        var closed = Policy(PlatformCapabilities.QqOneBot, new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["text"] = false });
        var live = new ToolVisibility(policies: new FakeResolver(closed));

        Check(live.Allows(settings, open, "chat.reply") == false, "热重载空窗：实时解析器已关闭 text 时，旧快照开着也不可见");
        var liveOpen = new ToolVisibility(policies: new FakeResolver(open));
        Check(liveOpen.Allows(settings, open, "chat.reply"), "实时解析器开着时旧快照开着才可见");
        // 没有快照也没有来源上下文时无法解析策略 —— 一律不可见（与改造前的口径逐字一致，不是 #115 引入的）。
        Check(!liveOpen.Allows(settings, null, "chat.reply"), "没有上下文可解析策略时 Fail-Closed");
        return Task.CompletedTask;
    }

    public static Task InventoryAppendsMcpWithoutChangingBuiltinAsync()
    {
        var builtin = ToolInventory.Builtin;
        Check(builtin.BuiltinCount == 26, "内建三族仍是 26 条（10/10/6）");
        Check(builtin.McpCount == 0 && builtin.Count == 26, "没有 MCP 时查询面就是内建目录");
        Check(builtin.Duplicates.Count == 0, "内建目录里不该有重名");

        var tool = new BotAgent.Domain.Mcp.McpToolInfo(
            Name: "lookup",
            QualifiedName: "demo.lookup",
            Description: "第一行\n第二行 " + new string('长', 200),
            InputSchemaJson: "{}",
            ServerId: "demo",
            ReadOnlyHint: true);
        var inventory = ToolInventory.Create(BotAgent.Services.Tools.ToolDirectory.Builtin, new[] { tool });

        Check(inventory.BuiltinCount == 26 && inventory.McpCount == 1 && inventory.Count == 27, "MCP 是追加来源，不改内建 26 条");
        Check(inventory.Entries[^1].Id == "demo.lookup", "MCP 条目排在最后且 id = QualifiedName");
        Check(inventory.TryGet("demo.lookup", out var spec) && spec.Origin == BotAgent.Domain.Tools.ToolOrigin.Mcp, "能按 id 查回 MCP 条目并标明来源");
        Check(spec.PromptVisible == false, "MCP 工具不进聊天提示词的可见清单（与原行为一致）");
        Check(!spec.Summary.Contains('\n') && spec.Summary.Length <= 121, "描述只留单行短摘要（外部来源不可信）");
        Check(spec.Parameters.Contains("MCP", StringComparison.Ordinal), "参数契约标注由服务器声明");

        var clash = new BotAgent.Domain.Mcp.McpToolInfo("web.search", "web.search", "撞名", "{}", "demo", false);
        var withClash = ToolInventory.Create(BotAgent.Services.Tools.ToolDirectory.Builtin, new[] { clash });
        Check(withClash.Duplicates.Contains("web.search"), "与内建撞名必须显式报告（不静默覆盖）");
        return Task.CompletedTask;
    }

    private static EffectivePlatformPolicy Policy(PlatformCapabilities capabilities, IReadOnlyDictionary<string, bool>? overrides = null)
        => new(
            new PlatformContext("qq"),
            Registered: true,
            Connected: true,
            Enabled: true,
            ChatEnabled: true,
            capabilities,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            overrides ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>());

    private sealed class FakeResolver(EffectivePlatformPolicy policy) : IPlatformPolicyResolver
    {
        public EffectivePlatformPolicy Resolve(PlatformContext context) => policy;

        public EffectivePlatformPolicy ResolveForChannel(string? channel) => policy;

        public bool IsChatEnabled(string? channel) => true;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
