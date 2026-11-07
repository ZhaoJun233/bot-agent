using System.Diagnostics;
using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;
using BotAgent.Mcp;

// Pure synthetic local-process probe. Never opens a bot DB, network endpoint or private messages.
var checks = 0;
var interpreter = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("BOTAGENT_TEST_PYTHON") ?? "python";
var tempRoot = Environment.GetEnvironmentVariable("BOTAGENT_TEST_TEMP") ?? AppContext.BaseDirectory;
var root = Path.Combine(tempRoot, "botagent-python-probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.Copy(Path.Combine(AppContext.BaseDirectory, "botagent_worker.py"), Path.Combine(root, "botagent_worker.py"));
File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "worker.py"), Path.Combine(root, "worker.py"));
var logs = new List<string>();
try
{
    var status = await PythonWorkerInterpreter.CheckAsync(interpreter, "3.11", CancellationToken.None);
    Console.WriteLine($"Interpreter readiness: {status.ReasonCode} version={status.Version ?? "none"}");
    Check(status.Available && status.Compatible, "trusted interpreter readiness");
    Check(!(await PythonWorkerInterpreter.CheckAsync(interpreter, "3.99", CancellationToken.None)).Compatible, "incompatible version rejected");
    foreach (var version in new[] { "3.10", "3.011", "3.11.0", "4.0", "3.100", ">=3.11" })
        Check((await PythonWorkerInterpreter.CheckAsync(interpreter, version, CancellationToken.None)).ReasonCode == "python_invalid_required_version", "required version syntax rejected");
    Check(!(await PythonWorkerInterpreter.CheckAsync(Path.Combine(root, "missing-python"), "3.11", CancellationToken.None)).Available, "missing interpreter is honest");

    var load = Path.Combine(root, "loaded");
    var unload = Path.Combine(root, "unloaded");
    var options = Options() with { Configuration = new JsonObject
    {
        ["loadMarker"] = load, ["unloadMarker"] = unload, ["secret"] = "synthetic-secret-never-log",
    } };
    Environment.SetEnvironmentVariable("BOTAGENT_WORKER_SYNTHETIC_SECRET", "synthetic-secret-never-log");
    await using (var source = new PythonWorkerToolSource(options, logs.Add))
    {
        Check(!File.Exists(load), "construction is metadata-only");
        Check(!options.ToString().Contains("synthetic-secret", StringComparison.Ordinal), "option display redacts config");
        var tools = await source.DiscoverAsync(CancellationToken.None);
        Check(tools.Count == 1 && tools[0].Name == "echo", "discover explicit allowlist only");
        Check(File.Exists(load) && source.State == McpConnectionState.Ready, "load hook precedes ready");
        Check(tools[0].ServerId == "fixture_g1", "server identity pins generation");
        Check(tools[0].IsLocalProcess && tools[0].SourceGeneration == "1", "locked snapshot carries local classification and generation");
        Check((await source.CallAsync("mcp.fixture.hidden", new(), CancellationToken.None)).ReasonCode == "mcp_not_authorized", "hidden tool denied at source");
        Check((await source.CallAsync("mcp.other.echo", new(), CancellationToken.None)).ReasonCode == "mcp_not_authorized", "cross plugin call denied");
        var echo = await source.CallAsync("mcp.fixture.echo", new JsonObject { ["value"] = "synthetic" }, CancellationToken.None);
        Check(echo.Ok && JsonNode.Parse(echo.Content!)?["value"]?.GetValue<string>() == "synthetic", "tool reply crosses protocol");
        var context = await Call(source, "context");
        Check(context.Ok && JsonNode.Parse(context.Content!)?["configuration"]?["secret"]?.GetValue<string>() == "synthetic-secret-never-log", "config delivered only to enabled worker");
        Check(!JsonNode.Parse(context.Content!)!["contextRepr"]!.GetValue<string>().Contains("synthetic-secret", StringComparison.Ordinal), "SDK repr redacts config");
        var env = await Call(source, "env");
        Check(env.Ok && JsonNode.Parse(env.Content!)?["present"]?.GetValue<bool>() == false, "host secrets not inherited");
        var error = await Call(source, "error");
        Check(!error.Ok && error.ReasonCode == "python_tool_error" && error.Content is null, "exception payload hidden");
        Check(!(await Call(source, "denied")).Ok, "undeclared SDK capability fails closed");
        Check((await Call(source, "noise")).Ok, "stderr noise drained without deadlock");
        Check((await source.CallAsync("mcp.fixture.echo", new JsonObject { ["value"] = new string('x', 40000) }, CancellationToken.None)).ReasonCode == "mcp_arguments_too_large", "arguments byte bound");
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => source.CallAsync("mcp.fixture.echo", new JsonObject { ["value"] = i }, CancellationToken.None)));
        Check(concurrent.All(r => r.Ok) && concurrent.Select(r => JsonNode.Parse(r.Content!)!["value"]!.GetValue<int>()).Order().SequenceEqual(Enumerable.Range(0, 6)), "parallel callers serialize correlation IDs");
    }
    Check(File.Exists(unload), "bounded unload hook runs on dispose");
    Check(logs.All(line => !line.Contains("synthetic-secret", StringComparison.Ordinal) && !line.Contains(root, StringComparison.Ordinal)), "logs contain only safe shapes");

    await using (var source = new PythonWorkerToolSource(Options() with { AllowedTools = Array.Empty<string>() }))
    {
        Check(!source.Enabled && (await source.DiscoverAsync(CancellationToken.None)).Count == 0, "empty grants never starts process");
    }
    await using (var source = new PythonWorkerToolSource(Options() with { MaxResultBytes = 1024 }))
    {
        await source.DiscoverAsync(CancellationToken.None);
        var result = await Call(source, "large");
        Check(result.ReasonCode == "mcp_result_too_large" && result.UnknownOutcome, "oversize executed result does not permit replay");
        Check((await Call(source, "echo")).Ok, "bounded result rejection preserves healthy channel");
    }

    foreach (var (mode, reason) in new[] { ("crash", "python_worker_exited"), ("malformed", "python_protocol_error"), ("wrong_id", "python_protocol_error"), ("frame", "python_frame_too_large"), ("stderr_flood", "python_worker_exited"), ("missing_is_error", "python_protocol_error") })
    {
        await using var source = new PythonWorkerToolSource(Options());
        var before = (await source.DiscoverAsync(CancellationToken.None)).Single();
        var admittedGeneration = long.Parse(before.SourceGeneration!, System.Globalization.CultureInfo.InvariantCulture);
        var failed = await Call(source, mode);
        Check(!failed.Ok && failed.ReasonCode == reason && failed.UnknownOutcome, mode + " safely faults channel");
        Check(source.State == McpConnectionState.Faulted, mode + " observable fault");
        Check((await Call(source, "echo")).ReasonCode == "mcp_not_discovered", mode + " no implicit call restart");
        var after = (await source.DiscoverAsync(CancellationToken.None)).Single();
        Check(before.ServerId != after.ServerId && (await Call(source, "echo")).Ok, mode + " discovery restart changes lease generation");
        var stale = await source.CallWithGenerationAsync("mcp.fixture.echo", new JsonObject { ["mode"] = "crash" }, admittedGeneration, CancellationToken.None);
        Check(stale.ReasonCode == "mcp_source_changed" && !stale.UnknownOutcome && source.State == McpConnectionState.Ready, "stale generation denied before side effect");
        Check((await source.CallWithGenerationAsync("mcp.fixture.echo", new(), source.Generation, CancellationToken.None)).Ok, "current process generation accepted");
    }
    await using (var source = new PythonWorkerToolSource(Options()))
    {
        await source.DiscoverAsync(CancellationToken.None);
        var result = await Call(source, "duplicate");
        // OS pipe segmentation may put the duplicate in the next read; it must never match a new id.
        if (result.Ok) result = await Call(source, "echo");
        Check(result.ReasonCode == "python_protocol_error" && result.UnknownOutcome, "duplicate response rejected without correlation confusion");
    }
    await using (var source = new PythonWorkerToolSource(Options() with { CallTimeout = TimeSpan.FromMilliseconds(400) }))
    {
        Check((await source.DiscoverAsync(CancellationToken.None)).Count == 1, "timeout fixture ready");
        var started = Stopwatch.StartNew();
        var failed = await Call(source, "sleep");
        Check(failed.ReasonCode == "mcp_timeout" && failed.UnknownOutcome, "local timeout distinct with unknown outcome");
        Check(started.Elapsed < TimeSpan.FromSeconds(5), "timeout kills bounded worker");
    }
    var marker = Path.Combine(root, "active-pid");
    await using (var source = new PythonWorkerToolSource(Options() with { Configuration = new JsonObject { ["callMarker"] = marker } }))
    {
        await source.DiscoverAsync(CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var active = source.CallAsync("mcp.fixture.echo", new JsonObject { ["mode"] = "sleep" }, cancel.Token);
        await WaitForFile(marker);
        var pid = int.Parse(await File.ReadAllTextAsync(marker));
        cancel.Cancel();
        var cancelled = false;
        try { await active; } catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && source.State == McpConnectionState.Faulted, "caller cancellation stays distinguishable");
        Check(!Running(pid), "cancel reaps worker process");
    }
    File.Delete(marker);
    await using (var source = new PythonWorkerToolSource(Options() with { Configuration = new JsonObject { ["callMarker"] = marker } }))
    {
        await source.DiscoverAsync(CancellationToken.None);
        var active = Call(source, "sleep");
        await WaitForFile(marker);
        var pid = int.Parse(await File.ReadAllTextAsync(marker));
        var started = Stopwatch.StartNew();
        await source.DisposeAsync();
        var failed = await active;
        Check(failed.ReasonCode == "python_disposed" && failed.UnknownOutcome, "dispose cancels active call without replay");
        Check(source.State == McpConnectionState.Disposed && !source.Enabled && !Running(pid), "dispose removes process and inventory");
        Check(started.Elapsed < TimeSpan.FromSeconds(5), "active drain bounded");
    }
    await using (var source = new PythonWorkerToolSource(Options() with { Configuration = new JsonObject { ["unloadDelay"] = 30 } }))
    {
        await source.DiscoverAsync(CancellationToken.None);
        var started = Stopwatch.StartNew();
        await source.DisposeAsync();
        Check(started.Elapsed < TimeSpan.FromSeconds(5), "wedged unload killed within bound");
    }

    var countMarker = Path.Combine(root, "side-effects");
    await using (var source = new PythonWorkerToolSource(Options() with { Configuration = new JsonObject { ["countMarker"] = countMarker }, MaxStartsPerMinute = 2 }))
    {
        await source.DiscoverAsync(CancellationToken.None);
        Check((await Call(source, "count")).UnknownOutcome && await File.ReadAllTextAsync(countMarker) == "1", "side effect crash executes exactly once");
        Check(!(await Call(source, "count")).Ok && await File.ReadAllTextAsync(countMarker) == "1", "failed side effect never replayed");
        await source.DiscoverAsync(CancellationToken.None);
        await Call(source, "crash");
        Check((await source.DiscoverAsync(CancellationToken.None)).Count == 0 && source.State == McpConnectionState.Faulted, "bounded restart budget opens circuit");
    }
    await using (var broken = new PythonWorkerToolSource(Options() with { Id = "broken" }))
    await using (var healthy = new PythonWorkerToolSource(Options() with { Id = "healthy" }))
    {
        await Task.WhenAll(broken.DiscoverAsync(CancellationToken.None), healthy.DiscoverAsync(CancellationToken.None));
        await broken.CallAsync("mcp.broken.echo", new JsonObject { ["mode"] = "crash" }, CancellationToken.None);
        Check((await healthy.CallAsync("mcp.healthy.echo", new(), CancellationToken.None)).Ok, "crashed plugin does not take healthy plugin down");
    }
    foreach (var entry in new[] { "../worker.py", "/worker.py", "sub/../worker.py", "./worker.py", "sub\\worker.py", "worker.txt" })
    {
        await using var source = new PythonWorkerToolSource(Options() with { EntryPoint = entry });
        Check((await source.DiscoverAsync(CancellationToken.None)).Count == 0, "unsafe entry path fails before launch");
    }
    foreach (var protocolCase in new[] { "version", "duplicates", "schema", "many", "name" })
    {
        await using var source = new PythonWorkerToolSource(Options() with { Configuration = new JsonObject { ["protocolCase"] = protocolCase } });
        Check((await source.DiscoverAsync(CancellationToken.None)).Count == 0 && source.State == McpConnectionState.Faulted,
            protocolCase + " invalid discovery is fail closed");
        Check(source.LastErrorCode is "python_protocol_version_mismatch" or "python_invalid_tools", "safe health reason retained");
    }
    foreach (var tools in new[] { new[] { "echo", "echo" }, new[] { "invalid.tool" }, new[] { new string('x', 65) }, Enumerable.Range(0, 65).Select(i => "tool" + i).ToArray() })
    {
        var rejected = false;
        try { _ = new PythonWorkerToolSource(Options() with { AllowedTools = tools }); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "constructor rejects malformed allowlist");
    }
    await using (var source = new PythonWorkerToolSource(Options() with { Configuration = new JsonObject { ["loadDelay"] = 30 }, CallTimeout = TimeSpan.FromMilliseconds(400) }))
    {
        Check((await source.DiscoverAsync(CancellationToken.None)).Count == 0 && source.LastErrorCode == "mcp_timeout", "load timeout faults without publishing tools");
    }
    Console.WriteLine($"PythonWorkerProbe: {checks} passed; synthetic local processes only.");
}
finally
{
    Environment.SetEnvironmentVariable("BOTAGENT_WORKER_SYNTHETIC_SECRET", null);
    Directory.Delete(root, recursive: true);
}

PythonWorkerOptions Options() => new("fixture", interpreter, root, "worker.py", new[] { "echo" }, TimeSpan.FromSeconds(5), 65536);
Task<McpCallResult> Call(PythonWorkerToolSource source, string mode) => source.CallAsync("mcp.fixture.echo", new JsonObject { ["mode"] = mode }, CancellationToken.None);
void Check(bool ok, string label)
{
    if (!ok) throw new InvalidOperationException("Probe failed: " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
static bool Running(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
static async Task WaitForFile(string path)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(10, timeout.Token);
}
