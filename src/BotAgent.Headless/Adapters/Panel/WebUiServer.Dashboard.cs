using System.Diagnostics;
using System.Text.Json.Nodes;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;
using BotAgent.Services.Ops;
using BotAgent.Services.Tools;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// GET /api/dashboard：**只读**的健康仪表盘（general-agent-platform-plan.md 批次 J）。
///
/// 它不做任何新统计：把**已经存在**的东西摊在一屏 ——
///   · 运行与连接（uptime / OneBot / 账号在线 / AI 总开关）；
///   · 会话与队列（会话数 / 在途回复 / 排队）；
///   · **模型延迟**（ReplyPipeline.LastGenerationMilliseconds，此前面板没用过）；
///   · 宿主（内存工作集 + 容器上限 / 1 分钟负载，走 IHostFacts 端口）；
///   · **工具目录**（批 A5：条数 / 高风险 / 需要批准 / 自检）；
///   · **会话权限元数据**（批 B4）与**轨迹容量**（批 C）。
///
/// 三条纪律：只读（无写入路径）、只给形状（没有任何会话内容）、不引图表库（数字 + 徽标）。
/// </summary>
public sealed partial class WebUiServer
{
    private JsonObject BuildDashboardPayload()
    {
        var s = _box.Current;
        var directory = ToolDirectory.Builtin;
        var chatPolicy = CurrentChatPolicy();

        var memory = new JsonObject
        {
            // 工作集：本进程实际占了多少（.NET 在容器里的“感觉”）
            ["usedBytes"] = WorkingSetBytes(),
            ["limitBytes"] = _hostFacts?.MemoryLimitBytes(),
        };

        var sessionPolicy = _sessionPolicies is null
            ? new JsonObject { ["available"] = false }
            : new JsonObject
            {
                ["available"] = true,
                ["sessions"] = _sessionPolicies.Count,
                ["stale"] = _sessionPolicies.StaleCount(chatPolicy.PolicyFingerprint),
                ["rebuilt"] = _sessionPolicies.RebuiltCount,
            };

        return new JsonObject
        {
            ["uptimeSeconds"] = (int)(Clock.Now - _startedAt).TotalSeconds,
            ["aiMode"] = s.AiModeEnabled,
            ["onebot"] = _gateway.IsConnected,
            ["accountOnline"] = _scheduler.AccountOnline,
            ["conversations"] = _registry.Snapshot().Count,
            ["inFlight"] = _reply.InFlightReplies,
            ["queued"] = _reply.QueuedReplies,
            ["latencyMs"] = _reply.LastGenerationMilliseconds,
            ["memory"] = memory,
            ["load"] = _hostFacts?.LoadAverage(),
            ["tools"] = new JsonObject
            {
                ["total"] = directory.Specs.Count,
                ["chat"] = directory.Chat.Count,
                ["qq"] = directory.Qq.Count,
                ["server"] = directory.Server.Count,
                ["highRisk"] = directory.Specs.Count(x => ToolDescriptor.AlwaysDenied(x.Category)),
                // 当前策略下“要有人批过才会执行”的那几个（默认 = demo.echo，且只在审批开着时）
                ["needApproval"] = directory.Chat.Count(x => chatPolicy.Policy.RequiresApproval(x)),
                ["executors"] = directory.Executors.Count,
                ["healthy"] = directory.IsHealthy,
            },
            ["sessionPolicy"] = sessionPolicy,
            ["traces"] = _traces is null
                ? new JsonObject { ["available"] = false }
                : new JsonObject
                {
                    ["available"] = true,
                    ["recent"] = _traces.DoneCount,
                    ["active"] = _traces.ActiveCount,
                    ["capacity"] = TurnTraceStore.Capacity,
                },
        };
    }

    /// <summary>本进程工作集（拿不到就给 null —— 非容器 / 受限环境下别把整页弄红）。</summary>
    private static long? WorkingSetBytes()
    {
        try
        {
            return Process.GetCurrentProcess().WorkingSet64;
        }
        catch
        {
            return null;
        }
    }
}
