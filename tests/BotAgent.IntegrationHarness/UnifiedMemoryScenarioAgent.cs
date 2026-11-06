using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private static string MemoryLikeTool() => new JsonObject
    {
        ["tool"] = "qq", ["action"] = "like", ["user_id"] = UnifiedMemoryFixture.Owner, ["times"] = 1
    }.ToJsonString();

    private static string MemoryFinal(string text) => new JsonObject { ["final"] = text }.ToJsonString();

    private static async Task CheckUnifiedMemoryAgentBaselineAsync(UnifiedMemoryFixture f)
    {
        var likes = f.Protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like");
        var baseline = await f.AgentAsync("M153_AGENT_BASELINE", UnifiedMemoryFixture.Target,
            MemoryFinal("M153_AGENT_BASELINE_OK"), initialTool: MemoryLikeTool());
        Check("M153.A01 pending memory does not enter actual Agent", !HasMemory(baseline));
        Check("M153.A02 non-memory Agent tool is really configured and executes on mock",
            f.Protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like") == likes + 1);
    }

    private static async Task CheckUnifiedMemoryAgentAsync(UnifiedMemoryFixture f)
    {
        var first = await f.AgentAsync("M153_AGENT_GRANTED", UnifiedMemoryFixture.Target, MemoryFinal(UnifiedMemoryFixture.AgentSentinel));
        Check("M153.A03 actual Agent sees confirmed preference with history off", HasPreference(first));
        Check("M153.A04 history off omits earlier non-memory final", !RequestText(first).Contains("M153_AGENT_BASELINE_OK"));
        var (status, _) = await PanelPostJsonAsync($"http://127.0.0.1:{f.HealthPort}/api/settings", "{\"agentServerKeepContext\":true}");
        UnifiedMemoryFixture.Require("M153.A05 history switch changes through public settings", status == 200);
        f.KeepHistory = true;
        var continued = await f.AgentAsync("M153_AGENT_HISTORY_ON", UnifiedMemoryFixture.Target, MemoryFinal("M153_AGENT_HISTORY_OK"));
        Check("M153.A06 history on retains same memory authorization", HasPreference(continued));
        Check("M153.A07 memory-derived Agent final not persisted into history", !RequestText(continued).Contains(UnifiedMemoryFixture.AgentSentinel));
        var likes = f.Protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like");
        var blocked = await f.AgentAsync("M153_AGENT_TOOL_BLOCK", UnifiedMemoryFixture.Target, MemoryLikeTool(), blocked: true);
        Check("M153.A08 restricted actual Agent still receives preference", HasPreference(blocked));
        Check("M153.A09 configured tool cannot execute under memory grant",
            f.Protocol.ActionsReceived.Count(a => a["action"]?.GetValue<string>() == "send_like") == likes);
        var unrelated = await f.AgentAsync("M153_AGENT_UNRELATED", UnifiedMemoryFixture.Unrelated, MemoryFinal("M153_AGENT_UNRELATED_OK"));
        Check("M153.A10 history setting cannot grant another conversation", !HasMemory(unrelated));
        var historyControl = await f.AgentAsync("M153_AGENT_HISTORY_CONTROL", UnifiedMemoryFixture.Unrelated, MemoryFinal("M153_AGENT_HISTORY_CONTROL_OK"));
        Check("M153.A10b non-memory history really carries ordinary final", RequestText(historyControl).Contains("M153_AGENT_UNRELATED_OK"));
    }

    private static async Task CheckUnifiedMemoryAgentAfterRevokeAsync(UnifiedMemoryFixture f)
    {
        var request = await f.AgentAsync("M153_AGENT_REVOKED", UnifiedMemoryFixture.Target,
            MemoryFinal("M153_AGENT_REVOKED_OK"), continuation: true);
        Check("M153.A11 revoked grant absent from actual Agent even with continuation", !HasMemory(request));
        Check("M153.A12 continuation cannot revive memory-derived Agent final", !RequestText(request).Contains(UnifiedMemoryFixture.AgentSentinel));
    }
}
