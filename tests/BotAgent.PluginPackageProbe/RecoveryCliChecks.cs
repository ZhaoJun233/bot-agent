using System.Diagnostics;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;

internal static class RecoveryCliChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "plugin-preflight-cli-" + Guid.NewGuid().ToString("N"));
        var packages = Path.Combine(root, "plugins");
        var forbiddenRuntime = Path.Combine(root, "must-not-initialize");
        var current = typeof(PluginPackageService).Assembly.Location;
        var unmarked = typeof(IBotPlugin).Assembly.Location;
        var directory = Path.Combine(packages, "test.cli");
        Directory.CreateDirectory(directory);
        var state = Path.Combine(directory, "installed.json");
        var manifest = new PluginPackageManifest("test.cli", "1.0.0", "probe.dll", "SyntheticPlugin",
            new string('a', 64), "https://example.com/plugin.zip");
        await File.WriteAllTextAsync(state, JsonSerializer.Serialize(new InstalledPluginPackage("test.cli", Active: manifest)));
        try
        {
            var legacy = await Run(unmarked);
            check(legacy.Exit == 0 && JsonDocument.Parse(legacy.Output).RootElement.GetProperty("compatible").GetBoolean(),
                "offline CLI accepts compatible legacy state");
            await new PluginPackageService(packages, new FakeHttp()).StageUninstallAsync("test.cli");
            var original = File.ReadAllBytes(state);
            var modern = await Run(current);
            var refused = await Run(unmarked);
            check(modern.Exit == 0 && refused.Exit == 3
                && JsonDocument.Parse(refused.Output).RootElement.GetProperty("entries")[0].GetProperty("code").GetString()
                    == "plugin_state_reader_incompatible", "offline CLI returns nonzero before incompatible candidate can start");
            check(File.ReadAllBytes(state).SequenceEqual(original), "CLI checks preserve installed state");
            check(!Directory.Exists(forbiddenRuntime), "CLI never bootstraps application storage or configuration");
        }
        finally { Directory.Delete(root, recursive: true); }

        async Task<(int Exit, string Output)> Run(string candidate)
        {
            var start = new ProcessStartInfo("dotnet")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { current, "--check-plugin-state", packages, "--candidate", candidate })
                start.ArgumentList.Add(argument);
            start.Environment["BOTAGENT_DATA_DIR"] = forbiddenRuntime;
            start.Environment["QQCHAT_DATA_DIR"] = forbiddenRuntime;
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            await error;
            return (process.ExitCode, await output);
        }
    }
}
