using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Tools;

namespace BotAgent.EngineProbe;

/// <summary>
/// #136 任务级工具作用域：<see cref="ToolScope" /> 与 <see cref="ServerToolAccess" /> 的行为。
///
/// 这些用例要钉住的是**三条安全性质**，不是实现细节：
///   · 作用域只能收窄（声明的名字必须已经在配置白名单里，否则不生效）；
///   · 未知名字静默忽略（宁可窄，不许报错后被当成“没声明”而放开）；
///   · 未声明 = 不限（旧调用行为逐字不变）。
/// 另外钉住“白名单只有一处算法”：<see cref="ServerToolAccess.ConfiguredTools" /> 就是
/// <c>ServerAgentRunner.ResolveAllowedTools</c> 的实现，docker 仍要两把锁。
///
/// 口径：纯合成 AppSettings，不读设置文件、不碰环境、不启动机器人。
/// </summary>
public static class ToolScopeTests
{
    public static Task ScopeSemanticsAsync()
    {
        var unrestricted = ToolScope.FromIds(null);
        Check(unrestricted.IsUnrestricted, "没声明作用域时是“不限”");
        Check(ToolScope.FromIds(Array.Empty<string>()).IsUnrestricted, "声明空列表等价于没声明（旧调用行为不变）");
        Check(unrestricted.Allows("bash"), "不限时任何工具都通过作用域这一层（真正的判定归白名单/闸门）");
        Check(unrestricted.Ids.Count == 0, "不限时 Ids 为空（日志里不会写出假的清单）");

        var scope = ToolScope.FromIds(new[] { "bash", " bash ", "", "read", "bash" });
        Check(!scope.IsUnrestricted, "声明了名字就是受限作用域");
        Check(scope.Ids.Count == 2, $"重复与空白项要去掉（实际 {scope.Ids.Count} 条：{string.Join(",", scope.Ids)}）");
        Check(scope.Allows("bash") && scope.Allows("read"), "作用域里的工具通过");
        Check(!scope.Allows("docker"), "作用域外的工具不通过");
        Check(!scope.Allows(null), "null 工具名在受限作用域里不通过（Fail-Closed）");
        return Task.CompletedTask;
    }

    public static Task ScopeOnlyNarrowsAsync()
    {
        var configured = new[] { "bash", "read", "write" };
        var scope = ToolScope.FromIds(new[] { "read", "docker", "no.such.tool" });

        var restricted = scope.Restrict(configured);
        Check(restricted.SetEquals(new[] { "read" }),
            $"作用域与白名单取交集：声明 read/docker/未知 → 只剩 read（实际 {string.Join(",", restricted)}）");
        Check(!restricted.Contains("docker"), "白名单里没有的工具不会因为写进作用域就冒出来");
        Check(ToolScope.Unrestricted.Restrict(configured).SetEquals(configured), "不限时白名单原样保留");

        var copy = ToolScope.Unrestricted.Restrict(configured);
        copy.Add("injected");
        Check(!ToolScope.Unrestricted.Restrict(configured).Contains("injected"), "Restrict 每次返回新集合，改它不影响下一次");
        return Task.CompletedTask;
    }

    public static Task ServerToolAccessNarrowsAsync()
    {
        var settings = new AppSettings
        {
            AgentServerTools = "bash,read,write",
            AgentServerDocker = true,
            AgentServerQqActions = "like,poke"
        };

        var open = ServerToolAccess.Resolve(settings, dockerAvailable: false);
        Check(open.Tools.SetEquals(new[] { "bash", "read", "write" }), "没声明作用域时白名单不变");
        Check(open.QqActions.SetEquals(new[] { "like", "poke" }), "没声明作用域时 qq 动作照旧");
        Check(!open.QqAllowed, "白名单里没有 qq 时 QqAllowed 为假（qq 能不能用看白名单）");
        Check(open.Scope.IsUnrestricted, "没声明时作用域是“不限”");

        settings.AgentServerTools = "bash,read,write,qq";
        var scoped = ServerToolAccess.Resolve(settings, dockerAvailable: false, new[] { "read" });
        Check(scoped.Tools.SetEquals(new[] { "read" }),
            $"作用域收窄到 read（实际 {string.Join(",", scoped.Tools)}）");
        Check(scoped.QqActions.Count == 0, "作用域里没有 qq 时 qq 动作被清空（提示词与执行同时失去它）");
        Check(!scoped.QqAllowed, "作用域里没有 qq 时 QqAllowed 为假");

        var qqScoped = ServerToolAccess.Resolve(settings, dockerAvailable: false, new[] { "qq" });
        Check(qqScoped.QqAllowed, "作用域里写了 qq 且白名单也有 qq 时，qq 可用");
        Check(qqScoped.Tools.SetEquals(new[] { "qq" }), "同一次作用域不会把其它工具带进来");

        var unknownOnly = ServerToolAccess.Resolve(settings, dockerAvailable: false, new[] { "no.such.tool" });
        Check(unknownOnly.Tools.Count == 0, "只声明未知工具 → 一个工具都不给（宁可窄，不报错）");
        return Task.CompletedTask;
    }

    public static Task ServerToolAccessKeepsDockerTwoLocksAsync()
    {
        var settings = new AppSettings { AgentServerTools = "", AgentServerDocker = true };
        Check(ServerToolAccess.ConfiguredTools(settings, dockerAvailable: false).Contains("bash"), "留空 = 全开（既有口径）");
        Check(!ServerToolAccess.ConfiguredTools(settings, dockerAvailable: false).Contains("docker"),
            "开关开着但容器里没有 docker → 白名单里不出现 docker（两把锁）");
        Check(ServerToolAccess.ConfiguredTools(settings, dockerAvailable: true).Contains("docker"),
            "两把锁同时开才保留 docker");

        settings.AgentServerDocker = false;
        Check(!ServerToolAccess.ConfiguredTools(settings, dockerAvailable: true).Contains("docker"),
            "面板开关关着时即使容器有 docker 也不给");
        return Task.CompletedTask;
    }

    public static Task McpScopeFiltersByNameAsync()
    {
        var scoped = ServerToolAccess.Resolve(new AppSettings(), dockerAvailable: false, new[] { "mcp.echo" });
        Check(scoped.AllowsMcp("mcp.echo"), "作用域里点名的 MCP 工具可用");
        Check(!scoped.AllowsMcp("mcp.other"), "作用域外的 MCP 工具不可用");
        Check(!scoped.AllowsMcp(null), "null 名字在受限作用域里不可用");

        var open = ServerToolAccess.Resolve(new AppSettings(), dockerAvailable: false);
        Check(open.AllowsMcp("mcp.anything"), "没声明作用域时 MCP 工具不受这一层限制");
        return Task.CompletedTask;
    }

    private static void Check(bool ok, string name)
    {
        if (ok)
        {
            Console.WriteLine("  ✅ " + name);
            return;
        }

        Console.WriteLine("  ✗ " + name);
        throw new InvalidOperationException(name);
    }
}
