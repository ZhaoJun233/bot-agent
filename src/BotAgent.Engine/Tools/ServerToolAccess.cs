using System;
using System.Collections.Generic;
using System.Linq;
using BotAgent.Domain.Tools;
using BotAgent.Services.Agent;

namespace BotAgent.Services.Tools;

/// <summary>
/// `//` 那条路的**工具可见性单入口**（#136）：一次任务到底能用哪些工具，只在这里算一次。
///
/// 之前这条路的白名单在两个地方各算一遍（<c>RunAsync</c> 与 <c>ExecuteToolAsync</c>），
/// QQ 动作又是第三处（<c>QqActionCatalog.ParseAllowed</c>），于是“提示词里列了什么”
/// 与“执行时放行什么”只能靠人工保证一致。现在两处都读这份结果：
///   · <see cref="Tools" /> —— 服务器工具（bash / read / write / docker…），docker 仍要两把锁；
///   · <see cref="QqActions" /> —— qq 动作集合（作用域里没有 <c>qq</c> 就是空）；
///   · <see cref="Scope" /> —— 任务级作用域，用来再卡 MCP 工具（它们的名字不在服务器白名单里）。
///
/// **作用域只能收窄**：<see cref="ToolScope.Restrict" /> 与配置白名单取交集，
/// 声明里写不存在的工具名不会报错，也绝不会因此多出任何一个工具。
/// </summary>
public sealed class ServerToolAccess
{
    private ServerToolAccess(HashSet<string> tools, HashSet<string> qqActions, ToolScope scope)
    {
        Tools = tools;
        QqActions = qqActions;
        Scope = scope;
    }

    /// <summary>这次可用的服务器工具（已按作用域收窄）。语义只读，别在调用方改它。</summary>
    public HashSet<string> Tools { get; }

    /// <summary>这次允许的 QQ 动作（作用域里没有 <c>qq</c> 时为空集）。</summary>
    public HashSet<string> QqActions { get; }

    /// <summary>任务级作用域（未声明 = <see cref="ToolScope.Unrestricted" />）。</summary>
    public ToolScope Scope { get; }

    /// <summary>qq 工具这次能不能用（白名单里有 qq 且作用域没把它去掉）。</summary>
    public bool QqAllowed => Tools.Contains("qq");

    /// <summary>某个 MCP 工具这次能不能用（作用域按工具全名卡；未声明 = 全放）。</summary>
    public bool AllowsMcp(string? qualifiedName) => Scope.Allows(qualifiedName);

    /// <summary>
    /// 按配置 + 容器能力 + 可选任务作用域解析一次。
    /// <paramref name="dockerAvailable" /> 是“这个容器里真的有 docker”，由宿主侧探测后传进来（Engine 不碰环境）。
    /// </summary>
    public static ServerToolAccess Resolve(AppSettings settings, bool dockerAvailable, IReadOnlyList<string>? declaredScope = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var scope = ToolScope.FromIds(declaredScope);
        var tools = scope.Restrict(ConfiguredTools(settings, dockerAvailable));
        var qqActions = scope.Allows("qq")
            ? QqActionCatalog.ParseAllowed(settings.AgentServerQqActions)
            : new HashSet<string>(StringComparer.Ordinal);
        return new ServerToolAccess(tools, qqActions, scope);
    }

    /// <summary>配置里的服务器工具白名单（不含作用域）：留空 = 全开；docker 要面板开关 + 容器能力两把锁。</summary>
    public static HashSet<string> ConfiguredTools(AppSettings settings, bool dockerAvailable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var allowed = ParseTools(settings.AgentServerTools);
        if (!settings.AgentServerDocker || !dockerAvailable)
        {
            allowed.Remove("docker");
        }

        return allowed;
    }

    private static HashSet<string> ParseTools(string raw)
    {
        var all = ServerToolSpecs.Names;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(all, StringComparer.Ordinal);
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = piece.Trim().ToLowerInvariant();
            var canonical = all.FirstOrDefault(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (canonical is not null)
            {
                set.Add(canonical);
            }
        }

        return set;
    }
}
