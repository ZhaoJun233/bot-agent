using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Tools;
using BotAgent.Mcp;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Mcp;
using BotAgent.Services.Ops;
using BotAgent.Services.Tools;

namespace BotAgent.McpProbe;

/// <summary>
/// Wave4 MCP 探针：
///   A. 真实 <see cref="McpToolSource" /> + 进程内回环合成服务器（发现/调用/超时/取消/重连/停机）；
///   B. Engine 的 <see cref="McpToolUseCase" /> 治理（授权/Schema/预算/审计/异常）；
///   C. Host 的 <c>McpConfiguration.Build</c> 与 <see cref="ServerAgentRunner" /> 接线。
///
/// 所有输入都是合成的（10001 / 群友A / example.com / synthetic-token）；不连公网、不读写数据目录。
/// </summary>
public static class Program
{
    private static readonly string[] AllowedTools = { "echo", "slow", "huge", "image", "fail", "malformed" };

    private static int _passed;
    private static int _failed;
    private static int _groupPassed;
    private static int _groupFailed;

    public static async Task<int> Main()
    {
        await RunGroupAsync("A. real McpToolSource + synthetic server", GroupAAsync);
        await RunGroupAsync("B. engine governance", GroupBAsync);
        await RunGroupAsync("C. config/host wiring", GroupCAsync);

        Console.WriteLine();
        Console.WriteLine($"MCP PROBE: pass={_passed} fail={_failed}");
        return _failed == 0 ? 0 : 1;
    }

    // ───────────────────────── group harness ─────────────────────────

    private static async Task RunGroupAsync(string name, Func<Task> body)
    {
        _groupPassed = 0;
        _groupFailed = 0;
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Check(false, $"group threw {ex.GetType().Name}: {ex.Message}");
        }

        Console.WriteLine($"GROUP {name}: pass={_groupPassed} fail={_groupFailed}");
    }

    private static async Task StepAsync(string name, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Check(false, $"{name} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Check(bool condition, string name)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (condition)
        {
            _passed++;
            _groupPassed++;
        }
        else
        {
            _failed++;
            _groupFailed++;
        }
    }

    // ───────────────────────── A. real source ─────────────────────────

    private static async Task GroupAAsync()
    {
        await StepAsync("A1 discovery", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            var tools = await harness.Source.DiscoverAsync(CancellationToken.None);

            Check(tools.Count == 6, $"discovery returns the 6 explicit tools (got {tools.Count})");
            var names = tools.Select(tool => tool.QualifiedName).ToHashSet(StringComparer.Ordinal);
            foreach (var name in AllowedTools)
            {
                Check(names.Contains("mcp.demo." + name), "qualified name present: mcp.demo." + name);
            }

            Check(!names.Contains("mcp.demo.unauthorized"), "unauthorized tool is not projected");
            Check(tools.All(tool => tool.Description.Length <= McpLimits.MaxDescriptionLength),
                "descriptions bounded by McpLimits.MaxDescriptionLength");
            Check(tools.All(tool => Encoding.UTF8.GetByteCount(tool.InputSchemaJson) <= McpLimits.MaxSchemaBytes),
                "schemas bounded by McpLimits.MaxSchemaBytes");
            Check(server.LastAuthorization == "Bearer " + SyntheticMcpServer.Token,
                "server saw the synthetic Bearer token");
            Check(server.LastProtocolHeader == "2025-11-25",
                $"server saw the pinned protocol header (got {server.LastProtocolHeader ?? "<null>"})");
            Check(server.RequestCount("initialize") == 1, "exactly one initialize handshake");
            Check(server.RequestCount("tools/list") >= 1, "tools/list was requested");
        });

        await StepAsync("A2 echo call", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var result = await harness.Source.CallAsync("mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, CancellationToken.None);

            Check(result.Ok && result.ReasonCode == "ok", $"echo succeeds (reason {result.ReasonCode})");
            Check(result.Content?.Contains("echo:hi", StringComparison.Ordinal) == true,
                "echo content contains echo:hi");
            Check(result.AuditDetail is not null &&
                  Regex.IsMatch(result.AuditDetail, @"^server=demo;tool=echo;status=ok;bytes=\d+;ms=\d+;unknown=0$"),
                $"audit detail is shape-only (got {result.AuditDetail ?? "<null>"})");
            Check(result.AuditDetail is not null &&
                  !result.AuditDetail.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal) &&
                  !result.AuditDetail.Contains("echo:hi", StringComparison.Ordinal),
                "audit detail contains neither token nor content");
        });

        await StepAsync("A3 unknown tool", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);
            var before = server.ToolCallCount;

            var result = await harness.Source.CallAsync("mcp.demo.nope", new JsonObject(), CancellationToken.None);

            Check(!result.Ok && result.ReasonCode == "mcp_unknown_tool",
                $"unknown qualified name is mcp_unknown_tool (got {result.ReasonCode})");
            Check(server.ToolCallCount == before, "unknown tool never reached the server");
        });

        await StepAsync("A4 result too large", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server, maxResultBytes: 1024);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var result = await harness.Source.CallAsync("mcp.demo.huge", new JsonObject(), CancellationToken.None);

            Check(!result.Ok && result.ReasonCode == "mcp_result_too_large",
                $"4000 chars exceeds maxResultBytes=1024 (got {result.ReasonCode})");
            Check(result.Content is null, "oversized result has no content");
        });

        await StepAsync("A5 tool error", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var result = await harness.Source.CallAsync("mcp.demo.fail", new JsonObject(), CancellationToken.None);

            Check(!result.Ok && result.ReasonCode == "mcp_tool_error",
                $"isError result maps to mcp_tool_error (got {result.ReasonCode})");
            Check(result.Content?.Contains("synthetic failure", StringComparison.Ordinal) == true,
                "tool error keeps the remote text for the model");
        });

        await StepAsync("A5b image-only result", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var result = await harness.Source.CallAsync("mcp.demo.image", new JsonObject(), CancellationToken.None);

            Check(result.Ok && result.ReasonCode == "ok",
                $"image-only result succeeds (reason {result.ReasonCode})");
            Check(result.Content?.Contains("已省略 1 个非文本内容块", StringComparison.Ordinal) == true,
                "image-only result reports one omitted non-text block");
            Check(result.Content?.Contains(SyntheticMcpServer.ImageData, StringComparison.Ordinal) != true,
                "image block base64 data is not leaked into model text");
        });

        await StepAsync("A6 malformed response", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var result = await harness.Source.CallAsync("mcp.demo.malformed", new JsonObject(), CancellationToken.None);

            Check(!result.Ok && result.ReasonCode is "mcp_protocol" or "mcp_transport",
                $"malformed JSON maps to protocol/transport (got {result.ReasonCode})");
            Check(result.UnknownOutcome, $"malformed response records UnknownOutcome=true (got {result.UnknownOutcome})");
            Check(result.AuditDetail is null ||
                  !result.AuditDetail.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal),
                "malformed response audit detail has no token");
        });

        await StepAsync("A7 timeout", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server, timeoutMs: 200);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var watch = Stopwatch.StartNew();
            var result = await harness.Source.CallAsync("mcp.demo.slow", new JsonObject(), CancellationToken.None);
            watch.Stop();

            Check(!result.Ok && result.ReasonCode == "mcp_timeout",
                $"2s tool with 200ms call timeout is mcp_timeout (got {result.ReasonCode})");
            Check(result.UnknownOutcome, "local timeout reports UnknownOutcome=true");
            Check(watch.ElapsedMilliseconds < 1500,
                $"local timeout returns quickly (elapsed {watch.ElapsedMilliseconds}ms)");
        });

        await StepAsync("A8 caller cancellation", async () =>
        {
            await using var server = new SyntheticMcpServer();
            var logs = new ConcurrentQueue<string>();
            await using var harness = new SourceHarness(server, timeoutMs: 5000, log: message => logs.Enqueue(message));
            await harness.Source.DiscoverAsync(CancellationToken.None);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var canceled = false;
            try
            {
                await harness.Source.CallAsync("mcp.demo.slow", new JsonObject(), cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            Check(canceled, "caller cancellation throws OperationCanceledException (not an mcp_timeout result)");
            Check(logs.All(message => !message.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal)),
                "caller cancellation logs no token");
        });

        await StepAsync("A9 protocol mismatch", async () =>
        {
            await using var server = new SyntheticMcpServer { ProtocolVersion = "2025-06-18" };
            await using var harness = new SourceHarness(server);

            var tools = await harness.Source.DiscoverAsync(CancellationToken.None);

            Check(tools.Count == 0, $"mismatched protocol yields 0 tools (got {tools.Count})");
            Check(harness.Source.State == McpConnectionState.Faulted,
                $"mismatched protocol leaves the source Faulted (got {harness.Source.State})");
            Check(server.ToolCallCount == 0, "no tools/call after protocol mismatch");
        });

        await StepAsync("A9b protocol 2024-11-05 mismatch", async () =>
        {
            await using var server = new SyntheticMcpServer { ProtocolVersion = "2024-11-05" };
            await using var harness = new SourceHarness(server);

            var tools = await harness.Source.DiscoverAsync(CancellationToken.None);

            Check(tools.Count == 0, $"2024-11-05 yields 0 tools (got {tools.Count})");
            Check(harness.Source.State == McpConnectionState.Faulted,
                $"2024-11-05 leaves the source Faulted (got {harness.Source.State})");
            Check(server.ToolCallCount == 0, "no tools/call after 2024-11-05 mismatch");
        });

        await StepAsync("A10 auth failure", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server, token: "wrong-token");

            var tools = await harness.Source.DiscoverAsync(CancellationToken.None);

            Check(tools.Count == 0, "wrong token yields 0 tools");
            Check(harness.Source.State == McpConnectionState.Faulted,
                $"wrong token leaves the source Faulted (got {harness.Source.State})");
            Check(server.LastAuthorization == "Bearer wrong-token", "server observed the wrong token");
        });

        await StepAsync("A11 reconnect / no replay", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);
            server.ExpireFirstCall = true;
            var before = server.ToolCallCount;

            var first = await harness.Source.CallAsync("mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, CancellationToken.None);

            Check(!first.Ok && first.UnknownOutcome,
                $"expired first call fails with UnknownOutcome=true (reason {first.ReasonCode}, unknown={first.UnknownOutcome})");
            Check(server.ToolCallCount == before + 1,
                $"failed call is sent exactly once (tools/call count {server.ToolCallCount}, expected {before + 1})");

            var second = await harness.Source.CallAsync("mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, CancellationToken.None);

            Check(second.Ok && second.Content?.Contains("echo:hi", StringComparison.Ordinal) == true,
                "next call reconnects and succeeds");
            Check(server.ToolCallCount == before + 2,
                $"next call is a new call, not a replay (tools/call count {server.ToolCallCount}, expected {before + 2})");
        });

        await StepAsync("A11b lowercase qualified names", () =>
        {
            var method = typeof(McpToolSource).GetMethod("Qualify", BindingFlags.NonPublic | BindingFlags.Static);
            Check(method is not null, "McpToolSource.Qualify is present");
            if (method is not null)
            {
                var claimed = new HashSet<string>(StringComparer.Ordinal);
                var name = (string?)method.Invoke(null, new object[] { "Demo", "EchoUpper", claimed });
                Check(name == "mcp.demo.echoupper",
                    $"qualified name is lowercase so ParseStep can reach it (got {name})");
                Check(claimed.Contains("mcp.demo.echoupper"), "lowercase name is claimed for collision checks");
            }

            return Task.CompletedTask;
        });

        await StepAsync("A12 shutdown", async () =>
        {
            await using var server = new SyntheticMcpServer();
            var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);

            var watch = Stopwatch.StartNew();
            await harness.Source.DisposeAsync();
            watch.Stop();

            Check(watch.ElapsedMilliseconds < 3000, $"dispose does not hang (elapsed {watch.ElapsedMilliseconds}ms)");
            Check(harness.Source.State == McpConnectionState.Disposed,
                $"disposed source reports Disposed (got {harness.Source.State})");

            var result = await harness.Source.CallAsync("mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, CancellationToken.None);

            Check(!result.Ok && result.ReasonCode == "mcp_disposed",
                $"call after dispose is mcp_disposed (got {result.ReasonCode})");
            Check(server.SessionDeleteCount >= 1,
                $"dispose ends the MCP session with DELETE (got {server.SessionDeleteCount})");
            await harness.DisposeAsync();
        });

        await StepAsync("A13 SSE-only legacy server", async () =>
        {
            await using var server = new SyntheticMcpServer { RejectPostWith405 = true };
            await using var harness = new SourceHarness(server);

            var tools = await harness.Source.DiscoverAsync(CancellationToken.None);

            Check(tools.Count == 0, $"405-on-POST server yields 0 tools (got {tools.Count})");
            Check(harness.Source.State == McpConnectionState.Faulted,
                $"405-on-POST server leaves the source Faulted (got {harness.Source.State})");
            Check(server.PostRequestCount > 0,
                $"client attempted Streamable HTTP POST (POST count {server.PostRequestCount})");
            Check(server.GetRequestCount == 0,
                $"no SSE GET fallback was attempted (GET count {server.GetRequestCount})");
        });

        await StepAsync("A14 dispose during initialize", async () =>
        {
            await using var server = new SyntheticMcpServer
            {
                InitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var harness = new SourceHarness(server);
            var discovery = harness.Source.DiscoverAsync(CancellationToken.None);
            await server.InitObserved.WaitAsync(TimeSpan.FromSeconds(2));

            var watch = Stopwatch.StartNew();
            await harness.Source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            watch.Stop();
            server.InitGate.TrySetResult();
            var tools = await discovery.WaitAsync(TimeSpan.FromSeconds(3));

            Check(tools.Count == 0, $"dispose during initialize publishes no tools (got {tools.Count})");
            Check(server.RequestCount("tools/list") == 0,
                $"no tools/list after dispose during initialize (got {server.RequestCount("tools/list")})");
            Check(harness.Source.State == McpConnectionState.Disposed,
                $"dispose during initialize reports Disposed (got {harness.Source.State})");
            Check(watch.ElapsedMilliseconds < 2000,
                $"dispose cancels the pending initialize promptly (elapsed {watch.ElapsedMilliseconds}ms)");

            var second = Stopwatch.StartNew();
            await harness.Source.DisposeAsync();
            second.Stop();
            Check(second.ElapsedMilliseconds < 1000,
                $"second dispose is idempotent and prompt (elapsed {second.ElapsedMilliseconds}ms)");
            await harness.DisposeAsync();
        });

        await StepAsync("A15 dispose during tools/list", async () =>
        {
            await using var server = new SyntheticMcpServer
            {
                ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var harness = new SourceHarness(server);
            var discovery = harness.Source.DiscoverAsync(CancellationToken.None);
            await server.ListObserved.WaitAsync(TimeSpan.FromSeconds(2));

            await harness.Source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            server.ListGate.TrySetResult();
            var tools = await discovery.WaitAsync(TimeSpan.FromSeconds(3));

            Check(tools.Count == 0, $"dispose during tools/list publishes no tools (got {tools.Count})");
            Check(harness.Source.State == McpConnectionState.Disposed,
                $"dispose during tools/list reports Disposed (got {harness.Source.State})");
            var after = await harness.Source.CallAsync("mcp.demo.echo", new JsonObject(), CancellationToken.None);
            Check(!after.Ok && after.ReasonCode == "mcp_disposed",
                $"call after dispose during tools/list is mcp_disposed (got {after.ReasonCode})");
            await harness.DisposeAsync();
        });

        await StepAsync("A16 dispose during call", async () =>
        {
            await using var server = new SyntheticMcpServer
            {
                CallGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            var harness = new SourceHarness(server);
            await harness.Source.DiscoverAsync(CancellationToken.None);
            var call = harness.Source.CallAsync("mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, CancellationToken.None);
            await server.CallObserved.WaitAsync(TimeSpan.FromSeconds(2));

            await harness.Source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            server.CallGate.TrySetResult();
            var result = await call.WaitAsync(TimeSpan.FromSeconds(3));

            Check(!result.Ok && result.ReasonCode == "mcp_disposed",
                $"in-flight call observes mcp_disposed (got {result.ReasonCode})");
            Check(result.UnknownOutcome, "in-flight call during dispose is marked unknown");
            Check(harness.Source.State == McpConnectionState.Disposed,
                $"source stays Disposed after in-flight call (got {harness.Source.State})");
            await harness.DisposeAsync();
        });

        await StepAsync("A17 cached connection stays Ready", async () =>
        {
            await using var server = new SyntheticMcpServer();
            await using var harness = new SourceHarness(server);
            var first = await harness.Source.DiscoverAsync(CancellationToken.None);
            Check(first.Count == 6 && harness.Source.State == McpConnectionState.Ready,
                $"discovery leaves source Ready (tools={first.Count}, state={harness.Source.State})");

            var call = await harness.Source.CallAsync("mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, CancellationToken.None);
            Check(call.Ok && harness.Source.State == McpConnectionState.Ready,
                $"successful cached call leaves source Ready (ok={call.Ok}, state={harness.Source.State})");

            var refreshed = await harness.Source.DiscoverAsync(CancellationToken.None);
            Check(refreshed.Count == first.Count && harness.Source.State == McpConnectionState.Ready,
                $"refresh over the cached client leaves source Ready (tools={refreshed.Count}, state={harness.Source.State})");
            Check(server.RequestCount("initialize") == 1,
                $"cached call and refresh reuse one initialize (got {server.RequestCount("initialize")})");
            Check(server.RequestCount("tools/list") == 2,
                $"refresh performs a second tools/list on the same session (got {server.RequestCount("tools/list")})");
        });

        await StepAsync("A18 caller cancellation restores state", async () =>
        {
            await using var server = new SyntheticMcpServer
            {
                InitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            await using var harness = new SourceHarness(server);
            using var cancellation = new CancellationTokenSource();
            var discovery = harness.Source.DiscoverAsync(cancellation.Token);
            await server.InitObserved.WaitAsync(TimeSpan.FromSeconds(2));

            cancellation.Cancel();
            var canceled = false;
            try
            {
                await discovery.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            server.InitGate.TrySetResult();
            Check(canceled, "caller cancellation during initialize still throws");
            Check(harness.Source.State != McpConnectionState.Connecting,
                $"caller cancellation does not leave state Connecting (got {harness.Source.State})");

            var retry = await harness.Source.DiscoverAsync(CancellationToken.None);
            Check(retry.Count == 6 && harness.Source.State == McpConnectionState.Ready,
                $"next discovery recovers to Ready (tools={retry.Count}, state={harness.Source.State})");
        });
    }

    // ───────────────────────── B. engine governance ─────────────────────────

    private static async Task GroupBAsync()
    {
        await StepAsync("B1 refresh projection", async () =>
        {
            var fake = new FakeMcpToolSource { Tools = new[] { EchoTool() } };
            var useCase = new McpToolUseCase(fake);

            var count = await useCase.RefreshAsync(CancellationToken.None);

            Check(count == 1 && useCase.ToolCount == 1, $"refresh projects one tool (count={count})");
            Check(useCase.Tools[0].QualifiedName == "mcp.demo.echo", "projected name is mcp.demo.echo");
            var prompt = useCase.PromptText();
            Check(prompt.Contains("mcp.demo.echo", StringComparison.Ordinal), "prompt contains the qualified name");
            Check(prompt.Contains("不可信数据", StringComparison.Ordinal), "prompt warns that descriptions are untrusted");
            Check(prompt.Contains("message*:string", StringComparison.Ordinal), "prompt contains the schema summary");
        });

        await StepAsync("B2 gate decisions", async () =>
        {
            var fake = new FakeMcpToolSource { Tools = new[] { EchoTool() } };
            var useCase = new McpToolUseCase(fake);
            await useCase.RefreshAsync(CancellationToken.None);

            Check(useCase.Check("group:10001", "mcp.demo.echo", 0).Allow, "allowed tool passes the gate");
            Check(useCase.Check("group:10001", "mcp.demo.missing", 0).ReasonCode == "mcp_unknown_tool",
                "unknown tool is mcp_unknown_tool");

            var disabled = new McpToolUseCase(new FakeMcpToolSource
            {
                Enabled = false,
                Tools = new[] { EchoTool() },
            });
            Check(await disabled.RefreshAsync(CancellationToken.None) == 0, "disabled source discovers nothing");
            Check(disabled.Check("group:10001", "mcp.demo.echo", 0).ReasonCode == "mcp_disabled",
                "disabled source is mcp_disabled");

            var budget = new McpToolUseCase(new FakeMcpToolSource { Tools = new[] { EchoTool() } }, maxCallsPerRun: 1);
            await budget.RefreshAsync(CancellationToken.None);
            Check(budget.Check("group:10001", "mcp.demo.echo", 1).ReasonCode == "budget_exhausted",
                "callIndex at maxCallsPerRun is budget_exhausted");
        });

        await StepAsync("B3 execute + shape audit", async () =>
        {
            var fake = new FakeMcpToolSource
            {
                Tools = new[] { EchoTool() },
                OnCall = (_, _, _) => Task.FromResult(new McpCallResult(
                    true, "ok", "fake-result", "server=demo;tool=echo;status=ok;bytes=11", false, 11, 1)),
            };
            var audit = new FakeAuditChain();
            var useCase = new McpToolUseCase(fake, audit);
            await useCase.RefreshAsync(CancellationToken.None);

            var outcome = await useCase.ExecuteAsync("group:10001", "mcp.demo.echo",
                new JsonObject { ["message"] = "synthetic-arg-value" }, 0, CancellationToken.None);

            Check(outcome.Ok && outcome.ReasonCode == "ok", "valid args execute successfully");
            Check(fake.CallCount == 1, $"fake source called exactly once (got {fake.CallCount})");

            var events = audit.Events.Where(item => item.EventType == "mcp_call").ToArray();
            Check(events.Length == 1, $"exactly one mcp_call audit event (got {events.Length})");
            Check(events.Length == 1 &&
                  !events[0].ActionDetail.Contains("synthetic-arg-value", StringComparison.Ordinal) &&
                  !events[0].ActionDetail.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal),
                "audit ActionDetail has no argument value and no token");
            Check(events.Length == 1 && events[0].ActionDetail.Contains("\"result\":\"ok\"", StringComparison.Ordinal),
                "audit ActionDetail records the shape/status");
        });

        await StepAsync("B4 schema negatives", async () =>
        {
            var fake = new FakeMcpToolSource { Tools = new[] { EchoTool() } };
            var useCase = new McpToolUseCase(fake);
            await useCase.RefreshAsync(CancellationToken.None);

            var cases = new (string Name, JsonObject Args, string Reason)[]
            {
                ("unknown field", new JsonObject { ["message"] = "hi", ["extra"] = "x" }, "mcp_unknown_field"),
                ("missing required", new JsonObject(), "mcp_missing_required"),
                ("wrong type", new JsonObject { ["message"] = 42 }, "mcp_bad_type"),
                ("oversized args", new JsonObject { ["message"] = new string('x', 40_000) }, "mcp_args_too_large"),
            };

            foreach (var item in cases)
            {
                var outcome = await useCase.ExecuteAsync("group:10001", "mcp.demo.echo",
                    item.Args, 0, CancellationToken.None);
                Check(!outcome.Ok && outcome.ReasonCode == item.Reason,
                    $"{item.Name} is {item.Reason} (got {outcome.ReasonCode})");
            }

            Check(fake.CallCount == 0, $"schema failures never reach the fake source (got {fake.CallCount})");
        });

        await StepAsync("B5 unknown outcome text", async () =>
        {
            var fake = new FakeMcpToolSource
            {
                Tools = new[] { EchoTool() },
                OnCall = (_, _, _) => Task.FromResult(new McpCallResult(
                    false, "mcp_timeout", null, "status=mcp_timeout;unknown=1", true, 0, 0)),
            };
            var useCase = new McpToolUseCase(fake);
            await useCase.RefreshAsync(CancellationToken.None);

            var outcome = await useCase.ExecuteAsync("group:10001", "mcp.demo.echo",
                new JsonObject { ["message"] = "hi" }, 0, CancellationToken.None);

            Check(!outcome.Ok && outcome.ReasonCode == "mcp_timeout", "unknown-outcome failure is mcp_timeout");
            Check(outcome.ModelText?.Contains("不要自动重试", StringComparison.Ordinal) == true,
                "model text tells the agent not to retry automatically");
        });

        await StepAsync("B6 fake throws", async () =>
        {
            var fake = new FakeMcpToolSource
            {
                Tools = new[] { EchoTool() },
                OnCall = (_, _, _) => Task.FromException<McpCallResult>(
                    new InvalidOperationException("synthetic-secret-arg-value")),
            };
            var audit = new FakeAuditChain();
            var logs = new ConcurrentQueue<string>();
            var useCase = new McpToolUseCase(fake, audit, message => logs.Enqueue(message));
            await useCase.RefreshAsync(CancellationToken.None);

            var outcome = await useCase.ExecuteAsync("group:10001", "mcp.demo.echo",
                new JsonObject { ["message"] = "synthetic-secret-arg-value" }, 0, CancellationToken.None);

            Check(!outcome.Ok && outcome.ReasonCode == "mcp_internal",
                $"thrown exception becomes mcp_internal (got {outcome.ReasonCode})");
            Check(outcome.ModelText?.Contains("不要自动重试", StringComparison.Ordinal) == true,
                "internal failure tells the agent not to retry");

            var events = audit.Events.Where(item => item.EventType == "mcp_call").ToArray();
            Check(events.Length == 1, $"internal failure writes one audit event (got {events.Length})");
            Check(events.Length == 1 &&
                  !events[0].ActionDetail.Contains("synthetic-secret-arg-value", StringComparison.Ordinal) &&
                  !events[0].ActionDetail.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal),
                "internal failure audit is shape-only");
            Check(logs.All(message => !message.Contains("synthetic-secret-arg-value", StringComparison.Ordinal)),
                "internal failure log has no exception/argument text");
        });

        await StepAsync("B7 static directory unchanged", async () =>
        {
            var directory = ToolDirectory.Builtin;
            Check(directory.Chat.Count == 10, $"chat tools remain 10 (got {directory.Chat.Count})");
            Check(directory.Qq.Count == 10, $"QQ actions remain 10 (got {directory.Qq.Count})");
            Check(directory.Server.Count == 6, $"server tools remain 6 (got {directory.Server.Count})");
            Check(directory.Specs.Count == 26, $"total static tools remain 26 (got {directory.Specs.Count})");
            Check(directory.IsHealthy, "static tool directory still passes its health check");
            Check(directory.Specs.All(spec => !spec.Id.StartsWith("mcp.", StringComparison.Ordinal)),
                "no MCP tool is statically injected into the builtin directory");
            await Task.CompletedTask;
        });
    }

    // ───────────────────────── C. config/host wiring ─────────────────────────

    private static async Task GroupCAsync()
    {
        await StepAsync("C1 private endpoint rejected by default", async () =>
        {
            await WithEnvironmentAsync(new Dictionary<string, string?>
            {
                ["BOTAGENT_MCP_ENABLED"] = "1",
                ["BOTAGENT_MCP_SERVERS"] = ServerJson("http://127.0.0.1:10001/mcp", new[] { "echo" }),
                ["BOTAGENT_MCP_ALLOW_PRIVATE"] = null,
            }, async () =>
            {
                var logs = new List<string>();
                var wiring = BuildWiring(new FakeSecretsRepository(), null, logs.Add);
                var useCase = UseCaseOf(wiring);
                var runtime = RuntimeOf(wiring);

                Check(runtime is null, "private endpoint without BOTAGENT_MCP_ALLOW_PRIVATE yields no runtime");
                Check(useCase.ToolCount == 0, "rejected private endpoint exposes 0 tools");
                await DisposeRuntimeAsync(runtime);
            });
        });

        await StepAsync("C2 private endpoint allowed", async () =>
        {
            await WithEnvironmentAsync(new Dictionary<string, string?>
            {
                ["BOTAGENT_MCP_ENABLED"] = "1",
                ["BOTAGENT_MCP_SERVERS"] = ServerJson("http://127.0.0.1:10001/mcp", new[] { "echo" }),
                ["BOTAGENT_MCP_ALLOW_PRIVATE"] = "1",
            }, async () =>
            {
                var logs = new List<string>();
                var wiring = BuildWiring(new FakeSecretsRepository(), null, logs.Add);
                var useCase = UseCaseOf(wiring);
                var runtime = RuntimeOf(wiring);

                Check(runtime is not null, "BOTAGENT_MCP_ALLOW_PRIVATE=1 accepts the private endpoint");
                Check(useCase.ToolCount == 0, "runtime is built without discovery");
                await DisposeRuntimeAsync(runtime);
            });
        });

        await StepAsync("C3 public endpoint with missing tokenSecret", async () =>
        {
            await WithEnvironmentAsync(new Dictionary<string, string?>
            {
                ["BOTAGENT_MCP_ENABLED"] = "1",
                ["BOTAGENT_MCP_SERVERS"] = ServerJson("https://mcp.example.com/mcp", new[] { "echo" }, "MCP_PROBE_TOKEN_MISSING"),
                ["BOTAGENT_MCP_ALLOW_PRIVATE"] = null,
                ["MCP_PROBE_TOKEN_MISSING"] = null,
            }, async () =>
            {
                var logs = new List<string>();
                var wiring = BuildWiring(new FakeSecretsRepository(), null, logs.Add);
                var useCase = UseCaseOf(wiring);
                var runtime = RuntimeOf(wiring);

                Check(runtime is null, "unresolved tokenSecret yields no runtime");
                Check(useCase.ToolCount == 0, "missing token exposes 0 tools");
                await DisposeRuntimeAsync(runtime);
            }, "MCP_PROBE_TOKEN_MISSING");
        });

        await StepAsync("C4 public endpoint with resolved tokenSecret", async () =>
        {
            var secrets = new FakeSecretsRepository();
            secrets.Values["MCP_PROBE_TOKEN"] = SyntheticMcpServer.Token;

            await WithEnvironmentAsync(new Dictionary<string, string?>
            {
                ["BOTAGENT_MCP_ENABLED"] = "1",
                ["BOTAGENT_MCP_SERVERS"] = ServerJson(
                    "https://mcp.example.com/mcp?token=" + SyntheticMcpServer.Token + "&x=secret-query",
                    new[] { "echo" }, "MCP_PROBE_TOKEN"),
                ["BOTAGENT_MCP_ALLOW_PRIVATE"] = null,
                ["MCP_PROBE_TOKEN"] = null,
            }, async () =>
            {
                var logs = new List<string>();
                var wiring = BuildWiring(secrets, null, logs.Add);
                var useCase = UseCaseOf(wiring);
                var runtime = RuntimeOf(wiring);

                Check(runtime is not null, "resolved tokenSecret builds a runtime");
                Check(useCase.ToolCount == 0, "runtime is built without discovery");
                await DisposeRuntimeAsync(runtime);

                Check(logs.Count > 0, "configuration logs its decisions");
                Check(logs.All(message =>
                        !message.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal) &&
                        !message.Contains("secret-query", StringComparison.Ordinal)),
                    "configuration log contains neither token nor URL query");
            }, "MCP_PROBE_TOKEN");
        });

        await StepAsync("C5 empty allowedTools", async () =>
        {
            await WithEnvironmentAsync(new Dictionary<string, string?>
            {
                ["BOTAGENT_MCP_ENABLED"] = "1",
                ["BOTAGENT_MCP_SERVERS"] = ServerJson("https://mcp.example.com/mcp", Array.Empty<string>()),
                ["BOTAGENT_MCP_ALLOW_PRIVATE"] = null,
            }, async () =>
            {
                var logs = new List<string>();
                var wiring = BuildWiring(new FakeSecretsRepository(), null, logs.Add);
                var useCase = UseCaseOf(wiring);
                var runtime = RuntimeOf(wiring);

                Check(runtime is null, "empty allowedTools yields no runtime");
                Check(useCase.ToolCount == 0, "empty allowedTools exposes 0 tools");
                await DisposeRuntimeAsync(runtime);
            });
        });

        await StepAsync("C6 ServerAgentRunner no_conversation", async () =>
        {
            var fake = new FakeMcpToolSource { Tools = new[] { EchoTool() } };
            var useCase = new McpToolUseCase(fake);
            await useCase.RefreshAsync(CancellationToken.None);

            var runner = (ServerAgentRunner)RuntimeHelpers.GetUninitializedObject(typeof(ServerAgentRunner));
            SetPrivateField(runner, "_box", new SettingsBox(new AppSettings()));
            SetPrivateField(runner, "_mcp", useCase);

            var outcome = await runner.ExecuteAsync(
                new ToolCall("mcp.demo.echo", new JsonObject { ["message"] = "hi" }), CancellationToken.None);

            Check(!outcome.Ok && outcome.ReasonCode == "no_conversation",
                $"runner rejects MCP tools without a conversation (got {outcome.ReasonCode})");
            Check(fake.CallCount == 0, "runner never calls the fake source on the no_conversation path");
        });

        await StepAsync("C7 McpArguments envelope", () =>
        {
            var method = typeof(ServerAgentRunner).GetMethod("McpArguments",
                BindingFlags.NonPublic | BindingFlags.Static);
            Check(method is not null, "ServerAgentRunner.McpArguments is present");
            if (method is null)
            {
                return Task.CompletedTask;
            }

            var raw = new JsonObject
            {
                ["tool"] = "mcp.demo.echo",
                ["name"] = "mcp.demo.echo",
                ["thought"] = "synthetic thought",
                ["final"] = "synthetic final",
                ["url"] = "https://example.com/page",
                ["message"] = "hi",
            };

            var result = (JsonObject)method.Invoke(null, new object[] { raw })!;

            Check(!result.ContainsKey("tool"), "McpArguments removes tool");
            Check(!result.ContainsKey("name"), "McpArguments removes name");
            Check(!result.ContainsKey("thought"), "McpArguments removes thought");
            Check(!result.ContainsKey("final"), "McpArguments removes final");
            Check(result["url"]?.GetValue<string>() == "https://example.com/page", "McpArguments keeps url");
            Check(result["message"]?.GetValue<string>() == "hi", "McpArguments keeps message");
            Check(raw.ContainsKey("tool") && raw.ContainsKey("final"), "McpArguments does not mutate the input object");
            return Task.CompletedTask;
        });

        await StepAsync("C8 contract ToString redaction", () =>
        {
            var options = new McpServerOptions("demo",
                "https://synthetic-user:synthetic-pass@mcp.example.com/mcp?token="
                + SyntheticMcpServer.Token + "&x=secret-query",
                SyntheticMcpServer.Token, new[] { "echo" }, TimeSpan.FromSeconds(5), 4096);
            var optionText = options.ToString();
            Check(!optionText.Contains(SyntheticMcpServer.Token, StringComparison.Ordinal)
                  && !optionText.Contains("secret-query", StringComparison.Ordinal)
                  && !optionText.Contains("synthetic-user", StringComparison.Ordinal)
                  && !optionText.Contains("synthetic-pass", StringComparison.Ordinal),
                "options ToString hides token, URL query, and userinfo");

            var result = new McpCallResult(true, "ok", "sensitive-synthetic-content");
            Check(!result.ToString().Contains("sensitive-synthetic-content", StringComparison.Ordinal),
                "call result ToString hides content");
            return Task.CompletedTask;
        });

        await StepAsync("C9 schema without properties rejects extra fields", () =>
        {
            var schema = """{"type":"object","additionalProperties":false}""";
            var allowed = McpSchemaValidator.TryValidate(schema, new JsonObject(), out var emptyReason);
            var rejected = McpSchemaValidator.TryValidate(schema, new JsonObject { ["x"] = 1 }, out var reason);
            Check(allowed && emptyReason == "ok", "empty args pass an object schema with no properties");
            Check(!rejected && reason == "mcp_unknown_field", $"extra field is rejected (got {reason})");
            return Task.CompletedTask;
        });
    }

    // ───────────────────────── helpers ─────────────────────────

    private static McpToolInfo EchoTool() => new(
        "echo",
        "mcp.demo.echo",
        "Echo the message back.",
        SyntheticMcpServer.EchoSchema,
        "demo",
        ReadOnlyHint: true);

    private sealed class SourceHarness : IAsyncDisposable
    {
        public SourceHarness(SyntheticMcpServer server, int timeoutMs = 3000, int maxResultBytes = McpLimits.DefaultResultBytes,
            Action<string>? log = null, string token = SyntheticMcpServer.Token, IReadOnlyList<string>? allowed = null)
        {
            Http = new HttpClient();
            IReadOnlyList<string> effectiveAllowed = allowed ?? AllowedTools;
            Source = new McpToolSource(new[]
            {
                new McpServerOptions("demo", server.Endpoint, token, effectiveAllowed,
                    TimeSpan.FromMilliseconds(timeoutMs), maxResultBytes),
            }, Http, log);
        }

        public HttpClient Http { get; }

        public McpToolSource Source { get; }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            Http.Dispose();
        }
    }

    private sealed class FakeMcpToolSource : IMcpToolSource
    {
        public bool Enabled { get; set; } = true;

        public McpConnectionState State { get; set; } = McpConnectionState.Ready;

        public IReadOnlyList<McpToolInfo> Tools { get; set; } = Array.Empty<McpToolInfo>();

        public int CallCount { get; private set; }

        public Func<string, JsonObject, CancellationToken, Task<McpCallResult>>? OnCall { get; set; }

        public Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
            => Task.FromResult(Tools);

        public Task<McpCallResult> CallAsync(string qualifiedName, JsonObject arguments, CancellationToken ct)
        {
            CallCount++;
            return OnCall is null
                ? Task.FromResult(new McpCallResult(true, "ok", "fake-result", "server=demo;tool=echo;status=ok", false, 11, 1))
                : OnCall(qualifiedName, arguments, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAuditChain : IAuditChain
    {
        public List<AuditEvent> Events { get; } = new();

        public void Append(AuditEvent auditEvent) => Events.Add(auditEvent);

        public AuditVerification Verify() => new(true, null, null, Events.Count);
    }

    private sealed class FakeSecretsRepository : ISecretsRepository
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

        public string? LoadApiKey() => Load("api-key");

        public bool SaveApiKey(string? apiKey) => Save("api-key", apiKey);

        public string? LoadAgentServerKey() => Load("agent-server-key");

        public bool SaveAgentServerKey(string? apiKey) => Save("agent-server-key", apiKey);

        public string? LoadNeteaseCookie() => Load("netease-cookie");

        public bool SaveNeteaseCookie(string? cookie) => Save("netease-cookie", cookie);

        public string? LoadTtsKey() => Load("tts-key");

        public bool SaveTtsKey(string? key) => Save("tts-key", key);

        public string? LoadOfficialSecret() => Load("official-secret");

        public bool SaveOfficialSecret(string? secret) => Save("official-secret", secret);

        public string? LoadFeishuSecret() => Load("feishu-secret");

        public bool SaveFeishuSecret(string? secret) => Save("feishu-secret", secret);

        public string? LoadFeishuEncryptKey() => Load("feishu-encrypt-key");

        public bool SaveFeishuEncryptKey(string? key) => Save("feishu-encrypt-key", key);

        public string? Load(string name)
            => Values.TryGetValue(name, out var value) ? value : null;

        public bool Save(string name, string? value)
        {
            if (value is null)
            {
                Values.Remove(name);
            }
            else
            {
                Values[name] = value;
            }

            return true;
        }
    }

    private static readonly string[] EnvironmentNames =
    {
        "BOTAGENT_MCP_ENABLED",
        "BOTAGENT_MCP_SERVERS",
        "BOTAGENT_MCP_ALLOW_PRIVATE",
    };

    private static async Task WithEnvironmentAsync(Dictionary<string, string?> values, Func<Task> body,
        params string[] extraNames)
    {
        var names = EnvironmentNames.Concat(extraNames).Distinct(StringComparer.Ordinal).ToArray();
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name,
                    values.TryGetValue(name, out var value) ? value : null);
            }

            await body();
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, saved[name]);
            }
        }
    }

    private static string ServerJson(string url, string[] allowed, string? tokenSecret = null)
    {
        var tools = string.Join(",", allowed.Select(name => "\"" + name + "\""));
        var token = tokenSecret is null ? string.Empty : ",\"tokenSecret\":\"" + tokenSecret + "\"";
        return "[{\"id\":\"demo\",\"url\":\"" + url + "\",\"allowedTools\":[" + tools + "]" + token + "}]";
    }

    private static object BuildWiring(ISecretsRepository secrets, IAuditChain? audit, Action<string> log)
    {
        var type = typeof(ServerAgentRunner).Assembly.GetType("BotAgent.Host.McpConfiguration", throwOnError: true)!;
        var method = type.GetMethod("Build", BindingFlags.Public | BindingFlags.Static)
                     ?? throw new InvalidOperationException("McpConfiguration.Build was not found");
        return method.Invoke(null, new object?[] { secrets, audit, log })!;
    }

    private static McpToolUseCase UseCaseOf(object wiring)
        => (McpToolUseCase)(wiring.GetType()
            .GetProperty("UseCase", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(wiring) ?? throw new InvalidOperationException("McpWiring.UseCase was not found"));

    private static object? RuntimeOf(object wiring)
        => wiring.GetType()
            .GetProperty("Runtime", BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(wiring);

    private static async Task DisposeRuntimeAsync(object? runtime)
    {
        if (runtime is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException($"field {name} was not found");
        field.SetValue(target, value);
    }
}
