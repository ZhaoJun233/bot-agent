using System.Diagnostics;
using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;
using BotAgent.Mcp;

if (args.Length != 1) throw new ArgumentException("Pass the local Python executable path.");
var python = Path.GetFullPath(args[0]);
var root = Path.Combine(AppContext.BaseDirectory, "synthetic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
File.Copy(Path.Combine(AppContext.BaseDirectory, "worker.py"), Path.Combine(root, "worker.py"));
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException("FAIL " + label);
    checks++; Console.WriteLine("PASS " + label);
}
PythonWorkerToolSource Source(string id) => new(new PythonWorkerOptions("cleanup_" + id, python, root,
    "worker.py", ["echo"], TimeSpan.FromMilliseconds(1500), 4096));
Task<McpCallResult> Call(PythonWorkerToolSource source, string id, JsonObject arguments)
    => source.CallAsync("mcp.cleanup_" + id + ".echo", arguments, default);
bool Alive(int id)
{
    try { using var process = Process.GetProcessById(id); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}
async Task<bool> DeadSoon(int id)
{
    var started = Stopwatch.StartNew();
    while (Alive(id) && started.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(25);
    return !Alive(id);
}
void CleanupFixtureChild(int id)
{
    try { using var process = Process.GetProcessById(id); if (!process.HasExited) { process.Kill(true); process.WaitForExit(3000); } }
    catch (ArgumentException) { }
}
var controlStart = new ProcessStartInfo(python) { UseShellExecute = false, CreateNoWindow = true };
foreach (var argument in new[] { "-I", "-c", "import time; time.sleep(120)" }) controlStart.ArgumentList.Add(argument);
using var control = Process.Start(controlStart) ?? throw new InvalidOperationException("Synthetic control did not start.");
try
{
    foreach (var mode in new[] { "child_normal", "child_crash", "child_timeout" })
    {
        var pid = 0;
        await using var source = Source(mode);
        try
        {
            Check((await source.DiscoverAsync(default)).Count == 1, "owned startup " + mode);
            var file = Path.Combine(root, mode + ".pid");
            var result = await Call(source, mode, new() { ["mode"] = mode, ["pidFile"] = file });
            pid = int.Parse(await File.ReadAllTextAsync(file));
            if (mode == "child_normal")
            {
                Check(result.Ok && Alive(pid), "ordinary child live until disposal");
                await source.DisposeAsync();
            }
            else Check(!result.Ok && source.State == McpConnectionState.Faulted, "crash or timeout faults generation " + mode);
            Check(await DeadSoon(pid), "ordinary child reaped " + mode);
            Check(!control.HasExited, "unrelated synthetic control preserved " + mode);
        }
        finally { if (pid > 0) CleanupFixtureChild(pid); }
    }
    foreach (var mode in new[] { "error_scalar", "error_missing_code", "error_wrong_code", "error_wrong_message", "error_valid" })
    {
        await using var source = Source(mode);
        Check((await source.DiscoverAsync(default)).Count == 1, "protocol startup " + mode);
        var result = await Call(source, mode, new() { ["mode"] = mode });
        Check(!result.Ok && source.State == (mode == "error_valid" ? McpConnectionState.Ready : McpConnectionState.Faulted),
            "strict error object shape " + mode);
    }
    await using (var source = Source("stderr"))
    {
        Check((await source.DiscoverAsync(default)).Count == 1, "stderr startup");
        var result = await Call(source, "stderr", new() { ["mode"] = "stderr_overflow" });
        Check(!result.Ok && source.State == McpConnectionState.Faulted, "stderr lifetime overflow faults worker");
    }
    Console.WriteLine($"PASS Python cleanup probe: {checks} checks ({(OperatingSystem.IsWindows() ? "Windows job" : "Unix process group")})");
}
finally
{
    if (!control.HasExited) { control.Kill(true); control.WaitForExit(3000); }
    Directory.Delete(root, recursive: true);
}
