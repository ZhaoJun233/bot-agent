using BotAgent.Domain.Mcp;
using BotAgent.Mcp;
using BotAgent.Services.Mcp;

namespace BotAgent.McpProbe;

internal static class Phase4McpChecks
{
    public static async Task RunAsync(Action<bool, string, string?> check)
    {
        await using var serverA = new SyntheticMcpServer();
        await using var serverB = new SyntheticMcpServer();

        // 1. ReadOnlyConfirmed derives strictly from explicit ReadOnlyTools, never from untrusted remote readOnlyHint
        var sourceA = new McpToolSource(new[]
        {
            new McpServerOptions(
                "srvA",
                serverA.Endpoint,
                SyntheticMcpServer.Token,
                new[] { "echo", "fail" },
                TimeSpan.FromSeconds(3),
                McpLimits.DefaultResultBytes,
                ReadOnlyTools: new[] { "echo" })
        }, new HttpClient(), _ => { });

        var toolsA = await sourceA.DiscoverAsync(CancellationToken.None);
        var echoTool = toolsA.FirstOrDefault(t => t.Name == "echo");
        var failTool = toolsA.FirstOrDefault(t => t.Name == "fail");
        check(
            echoTool is { ReadOnlyHint: true, ReadOnlyConfirmed: true },
            "ReadOnlyTools explicitly confirms read-only MCP tool",
            $"echo roHint={echoTool?.ReadOnlyHint} roConfirmed={echoTool?.ReadOnlyConfirmed}");
        check(
            failTool is { ReadOnlyConfirmed: false },
            "Unlisted tool never promotes ReadOnlyConfirmed from server annotations alone",
            $"fail roHint={failTool?.ReadOnlyHint} roConfirmed={failTool?.ReadOnlyConfirmed}");

        // Check untrusted readOnlyHint case: echo in SyntheticMcpServer has ReadOnlyHint=true, omit it from ReadOnlyTools
        await using var hintOnlySource = new McpToolSource(new[]
        {
            new McpServerOptions(
                "hintOnly",
                serverA.Endpoint,
                SyntheticMcpServer.Token,
                new[] { "echo" },
                TimeSpan.FromSeconds(3),
                McpLimits.DefaultResultBytes,
                ReadOnlyTools: Array.Empty<string>())
        }, new HttpClient(), _ => { });
        var hintTools = await hintOnlySource.DiscoverAsync(CancellationToken.None);
        var hintEcho = hintTools.FirstOrDefault(t => t.Name == "echo");
        check(
            hintEcho is { ReadOnlyHint: true, ReadOnlyConfirmed: false },
            "Untrusted server readOnlyHint=true alone stays ReadOnlyConfirmed=false",
            $"roHint={hintEcho?.ReadOnlyHint} roConfirmed={hintEcho?.ReadOnlyConfirmed}");

        // 2. ReloadableMcpToolSource atomic swap switches discovery/calls and disposes prior source
        var reloadable = new ReloadableMcpToolSource(sourceA);
        var sourceB = new McpToolSource(new[]
        {
            new McpServerOptions(
                "srvB",
                serverB.Endpoint,
                SyntheticMcpServer.Token,
                new[] { "echo" },
                TimeSpan.FromSeconds(3),
                McpLimits.DefaultResultBytes,
                ReadOnlyTools: new[] { "echo" })
        }, new HttpClient(), _ => { });

        var beforeSwap = await reloadable.DiscoverAsync(CancellationToken.None);
        check(beforeSwap.Any(t => t.ServerId == "srvA"), "ReloadableMcpToolSource serves initial source srvA", null);

        await reloadable.SwapAsync(sourceB, CancellationToken.None);
        check(sourceA.State == McpConnectionState.Disposed, "SwapAsync disposes prior IMcpToolSource cleanly", sourceA.State.ToString());

        var afterSwap = await reloadable.DiscoverAsync(CancellationToken.None);
        check(afterSwap.Count == 1 && afterSwap[0].QualifiedName == "mcp.srvb.echo",
            "ReloadableMcpToolSource serves swapped source srvB immediately",
            string.Join(",", afterSwap.Select(t => t.QualifiedName)));

        var callRes = await reloadable.CallAsync("mcp.srvb.echo", new System.Text.Json.Nodes.JsonObject { ["message"] = "hot" }, CancellationToken.None);
        check(callRes.Ok && (callRes.Content ?? string.Empty).Contains("echo:hot"), "ReloadableMcpToolSource executes calls against new source", callRes.ReasonCode);

        // 3. Independent ProbeServerAsync lists candidates without mutating active reloadable source
        var probe = await ReloadableMcpToolSource.ProbeServerAsync(
            new McpServerOptions("probe", serverA.Endpoint, SyntheticMcpServer.Token, new[] { "echo" }, TimeSpan.FromSeconds(3), 4096),
            new HttpClient(),
            CancellationToken.None);
        var discoveredB = await reloadable.DiscoverAsync(CancellationToken.None);
        check(probe.Ok && probe.Tools.Any(t => t.Name == "echo") && discoveredB.All(t => t.ServerId == "srvB"),
            "ProbeServerAsync inspects remote endpoint without mutating live ReloadableMcpToolSource",
            probe.ReasonCode);

        await reloadable.DisposeAsync();
        check(reloadable.State == McpConnectionState.Disposed && sourceB.State == McpConnectionState.Disposed,
            "Disposing ReloadableMcpToolSource disposes current target source", reloadable.State.ToString());

        // 4. Stale SourceGeneration & ReadOnlyConfirmed mutation invalidated in ExecuteChatAsync
        await using var serverC = new SyntheticMcpServer();
        var genSrc1 = new McpToolSource(new[]
        {
            new McpServerOptions("srvC", serverC.Endpoint, SyntheticMcpServer.Token, new[] { "echo" },
                TimeSpan.FromSeconds(3), McpLimits.DefaultResultBytes, ReadOnlyTools: new[] { "echo" })
        }, new HttpClient(), _ => { });
        var genReloadable = new ReloadableMcpToolSource(genSrc1);
        var genUseCase = new McpToolUseCase(genReloadable);
        await genUseCase.RefreshAsync(CancellationToken.None);
        var oldRoBinding = genUseCase.ChatToolBindings().Single(b => b.Definition.Spec.Id == "mcp.srvc.echo");

        // Swap to new source where "echo" is NO LONGER in ReadOnlyTools
        var genSrc2 = new McpToolSource(new[]
        {
            new McpServerOptions("srvC", serverC.Endpoint, SyntheticMcpServer.Token, new[] { "echo" },
                TimeSpan.FromSeconds(3), McpLimits.DefaultResultBytes, ReadOnlyTools: Array.Empty<string>())
        }, new HttpClient(), _ => { });
        await genReloadable.SwapAsync(genSrc2, CancellationToken.None);
        await genUseCase.RefreshAsync(CancellationToken.None);
        var staleOutcome = await oldRoBinding.ExecuteAsync("group:10001",
            new BotAgent.Domain.Tools.ToolCall("mcp.srvc.echo", new System.Text.Json.Nodes.JsonObject { ["message"] = "bypass" }),
            CancellationToken.None);
        check(!staleOutcome.Ok && staleOutcome.ReasonCode == "mcp_unavailable",
            "Stale ReadOnlyConfirmed MCP binding blocked after reload changes generation/readOnlyConfirmed",
            staleOutcome.ReasonCode);
        await genReloadable.DisposeAsync();

        // 5. ModelOutputParser parses 65..96 char MCP qualified tool names in inline call
        var longToolName = "mcp.srv_alpha." + new string('x', 70);
        var parsedLong = BotAgent.Domain.Reply.ModelOutputParser.Parse(
            "{\"suitability\":95,\"reply\":\"\",\"call\":{\"tool\":\"" + longToolName + "\",\"args\":{}}}",
            questionsEnabled: false);
        check(parsedLong.Result.Call is not null && parsedLong.Result.Call.ToolId == longToolName,
            "ModelOutputParser accepts valid 65..96 char MCP qualified tool names",
            $"len={longToolName.Length} parsed={parsedLong.Result.Call?.ToolId.Length}");
    }
}
