using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Plugins;

internal static class DeploymentPreflightChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var shell = Environment.GetEnvironmentVariable("BOTAGENT_TEST_SH") ?? "/bin/sh";
        if (!File.Exists(shell)) throw new InvalidOperationException("Set BOTAGENT_TEST_SH to a POSIX sh interpreter.");
        var plan = typeof(PluginPackageService).Assembly.GetType("BotAgent.Adapters.Panel.PanelDeploymentPlan")!;
        var helper = plan.GetMethod("HelperScript", BindingFlags.NonPublic | BindingFlags.Static)!;
        var currentImage = "sha256:" + new string('a', 64);
        var candidateImage = "sha256:" + new string('b', 64);
        var currentAssembly = typeof(PluginPackageService).Assembly.Location;
        var root = Path.Combine(Path.GetTempPath(), "deploy-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var scenario in new[] { "deploy", "rollback", "incompatible", "build-failure", "copy-failure",
                "stop-failure", "source-changed", "restore-failure", "backup-failure", "tag-failure", "compose-failure" })
            {
                var work = Path.Combine(root, scenario);
                var packages = Path.Combine(work, "plugins");
                var directory = Path.Combine(packages, "test.deployment");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "installed.json");
                var manifest = new PluginPackageManifest("test.deployment", "1.0.0", "probe.dll", "SyntheticPlugin",
                    new string('a', 64), "https://example.com/plugin.zip");
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new InstalledPluginPackage(manifest.Id, Active: manifest)));
                await new PluginPackageService(packages, new FakeHttp()).StageUninstallAsync(manifest.Id);
                var original = File.ReadAllBytes(path);
                var candidate = scenario is "incompatible" or "restore-failure" ? typeof(IBotPlugin).Assembly.Location : currentAssembly;
                var script = (string)helper.Invoke(null, [scenario != "rollback", candidateImage, currentImage, "/data/data/plugins"] )!;
                // Only relocate the helper's deployment workspace; execute the real generated control flow.
                script = script.Replace("work=/work", "work=" + Quote(work.Replace('\\', '/')));
                var file = Path.Combine(work, "helper.sh");
                await File.WriteAllTextAsync(file, MockDocker.Replace("\r\n", "\n") + "\n" + script.Replace("\r\n", "\n"));
                var start = new ProcessStartInfo(shell)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (OperatingSystem.IsWindows())
                    start.Environment["PATH"] = Path.GetDirectoryName(Path.GetFullPath(shell))
                        + Path.PathSeparator + start.Environment["PATH"];
                start.ArgumentList.Add(file);
                start.Environment["TEST_SCENARIO"] = scenario;
                start.Environment["TEST_CURRENT_IMAGE"] = currentImage;
                start.Environment["TEST_CANDIDATE_IMAGE"] = candidateImage;
                start.Environment["TEST_CANDIDATE"] = candidate;
                start.Environment["TEST_CURRENT_ASSEMBLY"] = currentAssembly;
                start.Environment["TEST_PLUGIN_ROOT"] = packages;
                start.Environment["BOTAGENT_DATA_DIR"] = Path.Combine(work, "forbidden-runtime");
                start.Environment["QQCHAT_DATA_DIR"] = Path.Combine(work, "forbidden-runtime");
                using var process = Process.Start(start)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                await stdout;
                var error = await stderr;
                var expected = scenario switch
                {
                    "deploy" or "rollback" => 0, "incompatible" => 3, "source-changed" => 4,
                    "restore-failure" => 70, _ => 41
                };
                var diagnostic = process.ExitCode == expected ? "" : File.ReadAllText(Path.Combine(work, "deploy.last.log"));
                check(process.ExitCode == expected, $"{scenario} exit={process.ExitCode}, expected={expected}; {error}; {diagnostic}");
                var calls = File.ReadAllLines(Path.Combine(work, "calls.log"));
                bool Called(string prefix) => calls.Any(line => line.StartsWith(prefix, StringComparison.Ordinal));
                var stopped = Array.FindIndex(calls, line => line.StartsWith("stop\t", StringComparison.Ordinal));
                var inspected = Array.FindIndex(calls, line => line.StartsWith("run\t", StringComparison.Ordinal));
                var switched = Array.FindIndex(calls, line => line.StartsWith("compose\t", StringComparison.Ordinal));
                check(inspected < 0 || stopped >= 0 && stopped < inspected, scenario + " freezes writer before inspection");
                check(switched < 0 || inspected >= 0 && inspected < switched, scenario + " gates adoption on inspection");
                check(!Called("start\tsynthetic-candidate"), scenario + " never starts extracted candidate");
                if (inspected >= 0)
                    check(calls[inspected].Contains("--entrypoint\tdotnet\t" + currentImage + "\t", StringComparison.Ordinal)
                        && calls[inspected].Contains("--volumes-from\tqqchat-bot:ro", StringComparison.Ordinal)
                        && calls[inspected].Contains("--network\tnone\t--read-only", StringComparison.Ordinal),
                        scenario + " uses current reader with isolated read-only mounts");
                if (scenario is "incompatible" or "restore-failure")
                    check(!Called("tag\t") && !Called("compose\t") && Called("start\tqqchat-bot"),
                        scenario + " refuses before tag changes and attempts original container recovery");
                if (scenario == "compose-failure")
                    check(!Called("start\t"), "failed adoption cannot blindly restart an older reader");
                if (scenario is "deploy" or "rollback")
                {
                    check(Called("tag\t" + candidateImage + "\tqqchat-agent:latest"), scenario + " publishes inspected immutable image");
                    check(Called("tag\t" + currentImage + (scenario == "deploy" ? "\tqqchat-agent:prev" : "\tqqchat-agent:rollback-current")),
                        scenario + " preserves the correct recovery image");
                    check(!Called("start\t"), scenario + " does not start old and new owners together");
                }
                check(File.ReadAllText(Path.Combine(work, "deploy.last.exit")).Trim() == "EXIT=" + expected,
                    scenario + " persists helper result");
                check(File.ReadAllBytes(path).SequenceEqual(original) && !Directory.Exists(Path.Combine(work, "forbidden-runtime")),
                    scenario + " preserves plugin state without business bootstrap");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    // Function shadowing makes every Docker interaction synthetic, including unexpected commands.
    private const string MockDocker = """
        docker() {
            first=1
            for argument in "$@"; do
                if [ "$first" = 0 ]; then printf '\t' >> "$work/calls.log"; fi
                printf '%s' "$argument" >> "$work/calls.log"
                first=0
            done
            printf '\n' >> "$work/calls.log"
            case "$1" in
                build)
                    [ "$TEST_SCENARIO" != build-failure ] || return 41
                    while [ "$1" != --iidfile ]; do shift; done
                    printf '%s' "$TEST_CANDIDATE_IMAGE" > "$2" ;;
                create) printf 'synthetic-candidate' ;;
                cp)
                    [ "$TEST_SCENARIO" != copy-failure ] || return 41
                    cp "$TEST_CANDIDATE" "$3" ;;
                inspect)
                    if [ "$TEST_SCENARIO" = source-changed ]; then printf 'different-image'
                    else printf '%s' "$TEST_CURRENT_IMAGE"; fi ;;
                stop) [ "$TEST_SCENARIO" != stop-failure ] || return 41 ;;
                run)
                    for candidate in "$work"/.deploy-preflight.*/candidate.dll; do
                        dotnet "$TEST_CURRENT_ASSEMBLY" --check-plugin-state "$TEST_PLUGIN_ROOT" --candidate "$candidate"
                    done ;;
                tag)
                    if [ "$TEST_SCENARIO" = backup-failure ] && [ "$3" = qqchat-agent:prev ]; then return 41; fi
                    if [ "$TEST_SCENARIO" = tag-failure ] && [ "$2" = "$TEST_CANDIDATE_IMAGE" ]; then return 41; fi ;;
                start) [ "$TEST_SCENARIO" != restore-failure ] || return 41 ;;
                compose) [ "$TEST_SCENARIO" != compose-failure ] || return 41 ;;
                rm) return 0 ;;
                *) return 91 ;;
            esac
        }
        """;
}
