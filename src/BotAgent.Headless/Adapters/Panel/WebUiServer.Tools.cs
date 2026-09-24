using System.Linq;
using System.Text.Json.Nodes;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Tools;
using BotAgent.Domain.Qq;
using BotAgent.Services.Tools;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// GET /api/tools：**只读**的“工具目录”视图（general-agent-platform-plan.md 批次 A5）。
///
/// 面板要能一眼回答三个问题：系统有哪些工具、谁能用（当前策略）、谁在执行（是否已接统一循环）。
/// 三条纪律：
///   · **只读**：这个端面对服务端零写入，也不改任何判定；
///   · **只给形状**：工具名 / 类别 / 参数说明 / 是否要审批 / 执行者 —— 不含任何会话内容；
///   · **判定口径照抄既有**：聊天那路的 allowed/needsApproval 用**同一个** ChatCapabilitySet.FromSwitches
///     按当前设置现算（与 ApprovalUseCase.Build 同一批参数），面板不自己发明白名单规则。
/// </summary>
public sealed partial class WebUiServer
{
    private JsonObject BuildToolsPayload()
    {
        var directory = ToolDirectory.Builtin;

        var chatPolicy = CurrentChatPolicy();

        var tools = new JsonArray();
        foreach (var spec in directory.Specs)
        {
            // 只有聊天那一路有“策略快照”这个概念（// 那路的放行由它自己的开关决定，面板不在这页复述）。
            var chatFamily = spec.Executor is ChatToolSpecs.ExecutorActions or ChatToolSpecs.ExecutorApproval;
            tools.Add(new JsonObject
            {
                ["id"] = spec.Id,
                ["category"] = spec.Category.ToString(),
                ["readOnly"] = spec.ReadOnly,
                ["executor"] = spec.Executor,
                ["defaultPolicy"] = spec.Default.ToString(),
                ["summary"] = spec.Summary,
                ["parameters"] = spec.Parameters,
                ["exception"] = spec.Exception,
                ["highRisk"] = ToolDescriptor.AlwaysDenied(spec.Category),
                ["allowlisted"] = chatFamily ? chatPolicy.Policy.AllowedTools.Contains(spec.Id) : null,
                ["needsApproval"] = chatFamily ? chatPolicy.Policy.RequiresApproval(spec) : null,
            });
        }

        var executors = new JsonArray();
        foreach (var executor in directory.Executors.All.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            executors.Add(new JsonObject
            {
                ["id"] = executor.Id,
                ["implementation"] = executor.Implementation,
                ["legacyPath"] = executor.LegacyPath,
                ["tools"] = directory.Specs.Count(x => x.Executor == executor.Id),
            });
        }

        return new JsonObject
        {
            ["count"] = directory.Specs.Count,
            ["chatCount"] = directory.Chat.Count,
            ["qqCount"] = directory.Qq.Count,
            ["serverCount"] = directory.Server.Count,
            ["healthy"] = directory.IsHealthy,
            ["duplicateIds"] = ToJsonArray(directory.DuplicateIds),
            ["missingExecutors"] = ToJsonArray(directory.MissingExecutors),
            ["unusedExecutors"] = ToJsonArray(directory.UnusedExecutors),
            ["missingExceptions"] = ToJsonArray(directory.MissingExceptions),
            ["executors"] = executors,
            // 会话级权限元数据（批次 B）：只有计数，没有 key。
            ["sessionPolicy"] = BuildSessionPolicyPayload(chatPolicy),
            ["tools"] = tools,
        };
    }

    /// <summary>
    /// 当前设置下的聊天策略快照（**唯一一处**给面板算策略的地方：工具目录页与仪表盘共用）。
    /// 参数与 Services/Permissions/ApprovalUseCase.cs 的 Build() 逐项对应 —— 面板不自己发明规则。
    /// </summary>
    private ChatCapabilitySet CurrentChatPolicy()
    {
        var s = _box.Current;
        return ChatCapabilitySet.FromSwitches(
            enableWebSearch: s.EnableWebSearch,
            enableMusic: s.EnableMusic,
            enableVoice: s.EnableVoice,
            enableStickers: s.EnableStickers,
            enablePoke: s.EnablePoke,
            scenario: s.ScenarioPreset,
            approvalsEnabled: s.EnableApprovals,
            questionsEnabled: s.EnableQuestions);
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }

    /// <summary>
    /// 会话级权限元数据的摘要（批次 B）：**只有计数，不含任何会话 key**。
    /// <paramref name="chatPolicy" /> 是当前设置下的策略快照 —— staleness 按**指纹**比（内容级），
    /// 不看版本号（版本是“内容变了才前进”的计数器，见 SessionPolicyStamp.IsStale 的注释）。
    /// </summary>
    private JsonObject BuildSessionPolicyPayload(ChatCapabilitySet chatPolicy)
    {
        if (_sessionPolicies is null)
        {
            return new JsonObject { ["available"] = false };
        }

        var stamps = _sessionPolicies.Snapshot();
        return new JsonObject
        {
            ["available"] = true,
            ["sessions"] = stamps.Count,
            ["rebuilt"] = _sessionPolicies.RebuiltCount,
            ["stale"] = _sessionPolicies.StaleCount(chatPolicy.PolicyFingerprint),
            ["official"] = stamps.Count(s => Channels.IsOfficial(s.Channel)),
            ["private"] = stamps.Count(s => !Channels.IsOfficial(s.Channel)),
        };
    }
}
