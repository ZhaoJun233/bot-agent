using System.Linq;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Qq;
using BotAgent.Services.Ops;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// GET /api/traces：**只读**的“一轮一条轨迹”（批次 C 的数据面）。
///
/// 它只给**形状**：runId / 会话（脱敏后）/ 六个节点（种类 / 状态码 / 耗时 / 工具名 / 原因码 / 计数）。
/// **不给正文、不给参数值** —— 轨迹不是聊天记录的第二份副本（§7.3 的字段红线）。
/// 会话 key 走面板既有的脱敏入口 <see cref="PanelDto.Mask" />（显示层脱敏、内部 key 不变）。
/// </summary>
public sealed partial class WebUiServer
{
    private JsonObject BuildTracesPayload(int limit)
    {
        if (_traces is null)
        {
            return new JsonObject { ["available"] = false };
        }

        var traces = new JsonArray();
        foreach (var trace in _traces.Recent(limit))
        {
            var nodes = new JsonArray();
            foreach (var node in trace.Nodes)
            {
                nodes.Add(new JsonObject
                {
                    ["kind"] = node.Kind.ToString(),
                    ["status"] = node.Status,
                    ["ms"] = node.DurationMs,
                    ["tool"] = node.ToolId,
                    ["reason"] = node.ReasonCode,
                    ["count"] = node.Count,
                });
            }

            traces.Add(new JsonObject
            {
                ["runId"] = trace.RunId,
                ["key"] = _dto.Mask(trace.ConversationKey),
                ["channel"] = Channels.Tag(Channels.ChannelOf(trace.ConversationKey)),
                ["startedAt"] = trace.StartedAt.ToUnixTimeMilliseconds(),
                ["outcome"] = trace.Outcome,
                ["totalMs"] = trace.TotalMs,
                ["nodes"] = nodes,
            });
        }

        return new JsonObject
        {
            ["available"] = true,
            ["count"] = traces.Count,
            ["active"] = _traces.ActiveCount,
            ["capacity"] = TurnTraceStore.Capacity,
            ["traces"] = traces,
        };
    }
}
