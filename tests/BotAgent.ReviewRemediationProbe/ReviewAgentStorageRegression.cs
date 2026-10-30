using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;

namespace BotAgent.ReviewRemediationProbe;

// Parent wiring: call RunAsync(Check) after synthetic AppDatabase.Initialize();
// dispatch TryRunChild(args) before initialization for bounded process/cancellation fixtures.
internal static class ReviewAgentStorageRegression
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        Profiles(check);
        Roles(check);
        Ownership(check);
        await AdmissionFailuresAsync(check);
        await ShellAsync(check);
    }

    private static void Profiles(Action<bool, string> check)
    {
        var profiles = new MemberProfileStore();
        var official = "8" + new string('0', 14) + "1";
        const string ordinary = "11001";
        const long group = 11002;
        void Append(string uid, long first, int count, long gid = group)
        {
            for (var i = 0; i < count; i++)
                profiles.Append(uid, "synthetic", "synthetic message", DateTimeOffset.UnixEpoch, "synthetic", gid, first + i);
        }
        Append(official, 1, 3);
        Append(ordinary, 1, 19);
        check(!profiles.FindSummarizable(20, 100).Any(c => c.Uid == official || c.Uid == ordinary),
            "B17 ordinary 19 and official 3 remain below their thresholds");
        Append(official, 4, 1);
        var first = profiles.FindSummarizable(20, 100).Single(c => c.Uid == official);
        check(first.NewMessages.Count == 4 && first.ThroughSeq == 4 && first.FoldedCount == 4,
            "B17 official four-message batch is not rejected by ordinary threshold");
        profiles.ApplySummary(official, first.Scope, "synthetic summary", first.ThroughSeq, first.FoldedCount);
        Append(official, 5, 3);
        check(!profiles.FindSummarizable(20, 100).Any(c => c.Uid == official), "B17 summary boundary excludes folded messages");
        Append(official, 8, 1);
        var next = profiles.FindSummarizable(20, 100).Single(c => c.Uid == official);
        check(next.NewMessages.Count == 4 && next.ThroughSeq == 8 && next.FoldedCount == 8 && next.ExistingSummary == "synthetic summary",
            "B17 official threshold remains effective after prior summary");
        Append(ordinary, 20, 1);
        check(profiles.FindSummarizable(20, 100).Single(c => c.Uid == ordinary).NewMessages.Count == 20,
            "B17 ordinary twenty-message threshold is unchanged");
        Append(official, 1, 4, group + 1);
        check(profiles.FindSummarizable(20, 100).Count(c => c.Uid == official) == 2,
            "B17 official candidate summary boundaries remain scope-isolated");
    }

    private static void Roles(Action<bool, string> check)
    {
        var roles = new MemberRoleStore(_ => { });
        const string uid = "10003";
        const long group = 11002;
        roles.Remember(uid, group, "member", "synthetic title", "synthetic", titleChecked: true);
        var lookup = roles.Find(uid, group)!;
        roles.Remember(uid, group, "admin", null, "synthetic renamed");
        var eventRow = roles.Find(uid, group)!;
        check(eventRow.Role == "admin" && eventRow.Title == "synthetic title" && eventRow.TitleChecked && !roles.NeedsRefresh(uid, group),
            "B18 role-only event preserves checked title and fresh cache");
        check(eventRow.UpdatedAt == lookup.UpdatedAt && eventRow.Name == "synthetic renamed",
            "B18 role/name event cannot renew authoritative title freshness");
        AppDatabase.Write(conn => AppDatabase.Exec(conn,
            "UPDATE member_roles SET updated_unix = $old WHERE uid = $u AND group_id = $g",
            ("$old", lookup.UpdatedAt.Subtract(TimeSpan.FromDays(4)).ToUnixTimeSeconds()), ("$u", uid), ("$g", group)));
        roles.Remember(uid, group, "owner", null, null);
        check(roles.NeedsRefresh(uid, group) && roles.Find(uid, group)!.Title == "synthetic title",
            "B18 stale title stays due despite new role event");
        roles.Remember(uid, group, "owner", "", "synthetic", titleChecked: true);
        roles.Remember(uid, group, "member", null, "synthetic");
        check(roles.Find(uid, group) is { Title.Length: 0, TitleChecked: true } && !roles.NeedsRefresh(uid, group),
            "B18 authoritative empty title remains a checked cache hit after event");
        roles.Remember("10004", group, "member", null, "synthetic");
        check(roles.NeedsRefresh("10004", group), "B18 new role-only record still requests first title lookup");
    }

    private static readonly Type AdmissionType = typeof(AgentCommandService).Assembly.GetType("BotAgent.Services.Agent.ServerAgentAdmission", true)!;

    private static IDisposable Acquire(string key, ConcurrentDictionary<string, bool> busy,
        ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)> current)
        => (IDisposable)(AdmissionType.GetMethod("TryAcquire")!.Invoke(null, new object[] { key, busy, current })
            ?? throw new InvalidOperationException("synthetic_admission_denied"));

    private static AgentTask NewTask(string id, string key) => new() { Id = id, SourceKey = key, Prompt = "synthetic", Session = "" };

    private static void Ownership(Action<bool, string> check)
    {
        const string key = "group:10002";
        var busy = new ConcurrentDictionary<string, bool>();
        var current = new ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)>();
        var old = Acquire(key, busy, current);
        AdmissionType.GetMethod("Bind")!.Invoke(old, new object[] { NewTask("old", key) });
        check(AdmissionType.GetMethod("TryAcquire")!.Invoke(null, new object[] { key, busy, current }) is null,
            "B20 same source rejects concurrent admission");
        old.Dispose();
        check(!busy.ContainsKey(key) && !current.ContainsKey(key), "B20 release removes current before source is reusable");
        using var successor = Acquire(key, busy, current);
        var next = NewTask("next", key);
        AdmissionType.GetMethod("Bind")!.Invoke(successor, new object[] { next });
        old.Dispose();
        check(busy.ContainsKey(key) && ReferenceEquals(current[key].Task, next), "B20 repeated old cleanup cannot release successor");
        current[key].Cts.Cancel();
        check(current[key].Cts.IsCancellationRequested, "B20 successor cancel handle remains usable");

        var mismatchBusy = new ConcurrentDictionary<string, bool>();
        var mismatchCurrent = new ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)>();
        using var owner = Acquire(key, mismatchBusy, mismatchCurrent);
        AdmissionType.GetMethod("Bind")!.Invoke(owner, new object[] { NewTask("owner", key) });
        using var replacementCts = new CancellationTokenSource();
        mismatchCurrent[key] = (next, replacementCts); // Forced mismatch tests exact-value removal, not frequency.
        owner.Dispose();
        check(ReferenceEquals(mismatchCurrent[key].Cts, replacementCts), "B20 mismatching current value is never removed or disposed");
    }

    private static async Task AdmissionFailuresAsync(Action<bool, string> check)
    {
        var sessions = DispatchProxy.Create<IAgentSessionStore, AgentStorageSessionProxy>();
        var proxy = (AgentStorageSessionProxy)(object)sessions;
        var box = new SettingsBox(new AppSettings());
        var ackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackCalls = 0;
        var runner = new ServerAgentRunner(box, null!, null!, _ => { });
        var registry = new ConversationRegistry(null!, box, null!, _ => true, _ => { });
        var service = new AgentCommandService(box, null!, null!, registry, null!, sessions, runner, null!,
            new AgentHooks(_ => { }, () => 10001, _ => false, (_, _) =>
            {
                if (Interlocked.Increment(ref ackCalls) == 1) { ackEntered.TrySetResult(); return ackRelease.Task; }
                return Task.CompletedTask;
            }));
        var conversation = new BotConversation { SourceKey = "group:10002", Name = "synthetic" };
        var message = new QqChatMessage(10001, true, 10001, 10002, "synthetic", "synthetic", DateTimeOffset.UnixEpoch, false);
        var busy = Field<ConcurrentDictionary<string, bool>>(service, "_serverAgentBusy");
        var current = Field<ConcurrentDictionary<string, (AgentTask Task, CancellationTokenSource Cts)>>(service, "_serverAgentCurrent");
        var run = typeof(AgentCommandService).GetMethod("RunOnServerAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Task InvokeRun() => (Task)run.Invoke(service, new object?[] { conversation, message, "synthetic", null, false })!;
        async Task ExpectFailure(Task pending, string label)
        {
            var failed = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { failed = true; }
            check(failed, label);
        }
        proxy.FailAt = "EnsureCurrent";
        await ExpectFailure(InvokeRun(), "B21 session setup exception is surfaced");
        check(busy.IsEmpty && current.IsEmpty, "B21 pre-binding setup failure releases busy");
        proxy.FailAt = "StartRun";
        await ExpectFailure(InvokeRun(), "B21 run persistence exception is surfaced");
        check(busy.IsEmpty && current.IsEmpty, "B21 post-binding persistence failure releases busy and CTS");
        proxy.FailAt = null;
        ackCalls = 0;
        var pending = InvokeRun();
        var arrived = await Task.WhenAny(ackEntered.Task, pending);
        if (arrived == pending) await pending;
        await ackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check(current.ContainsKey(conversation.SourceKey) && busy.ContainsKey(conversation.SourceKey),
            "B21 deterministic acknowledgement barrier holds admitted current");
        try
        {
            var stop = typeof(AgentCommandService).GetMethod("TryHandleAgentSubCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task<bool>)stop.Invoke(service, new object?[] { conversation, message, "stop", "server", null })!;
            check(current[conversation.SourceKey].Cts.IsCancellationRequested && current[conversation.SourceKey].Task.CancelRequested,
                "B20 stop reaches current cancellation while acknowledgement is blocked");
        }
        finally { ackRelease.TrySetException(new InvalidOperationException("synthetic_ack_failure")); }
        await ExpectFailure(pending, "B21 acknowledgement failure is surfaced");
        check(busy.IsEmpty && current.IsEmpty && proxy.Finished == 1,
            "B21 acknowledgement failure releases admitted state and finishes persisted run");
    }

    private static T Field<T>(object owner, string name)
        => (T)owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    public static int? TryRunChild(string[] args)
    {
        if (args.Length is < 1 or > 2 || args[0] is not ("--review-shell-output" or "--review-shell-block" or "--review-shell-tree")) return null;
        if (args[0] == "--review-shell-tree" && args.Length != 2) return 2;
        if (args[0] == "--review-shell-output")
        {
            var output = new string('o', 1024);
            var error = new string('e', 1024);
            for (var i = 0; i < 256; i++) { Console.Out.Write(output); Console.Error.Write(error); }
            return 7;
        }
        Console.WriteLine(Environment.ProcessId);
        Console.Out.Flush();
        if (args[0] == "--review-shell-tree")
        {
            var start = Child("--review-shell-block");
            start.RedirectStandardOutput = false;
            start.RedirectStandardError = false;
            using var descendant = Process.Start(start) ?? throw new InvalidOperationException("synthetic_child_start");
            File.WriteAllText(args[1] + ".pending", Environment.ProcessId + "," + descendant.Id);
            File.Move(args[1] + ".pending", args[1]);
        }
        else if (args.Length == 2) File.WriteAllText(args[1], Environment.ProcessId.ToString());
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static ProcessStartInfo Child(string mode)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(ReviewAgentStorageRegression).Assembly.Location);
        start.ArgumentList.Add(mode);
        foreach (var key in start.Environment.Keys.Where(IsSensitiveEnvironment).ToArray()) start.Environment.Remove(key);
        return start;
    }

    private static bool IsSensitiveEnvironment(string key) => key == "HEALTH_PORT" ||
        new[] { "BOTAGENT_", "QQCHAT_", "OPENAI_", "MINIMAX_", "TTS_", "HARNESS_" }.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static readonly Type ShellType = typeof(ServerAgentRunner).Assembly.GetType("BotAgent.Services.Agent.BoundedShellProcess", true)!;

    private static async Task<object> ShellRunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        var pending = (Task)ShellType.GetMethod("RunAsync")!.Invoke(null, new object[] { start, timeout, ct })!;
        await pending.WaitAsync(TimeSpan.FromSeconds(15));
        return pending.GetType().GetProperty("Result")!.GetValue(pending)!;
    }

    private static T Property<T>(object result, string name) => (T)result.GetType().GetProperty(name)!.GetValue(result)!;

    private static async Task ShellAsync(Action<bool, string> check)
    {
        var result = await ShellRunAsync(Child("--review-shell-output"), TimeSpan.FromSeconds(10), CancellationToken.None);
        check(Property<int?>(result, "ExitCode") == 7 && Property<string?>(result, "Failure") is null,
            "B24 simultaneous finite pipe output exits without deadlock and preserves nonzero exit");
        check(Property<string>(result, "Stdout").Length == 4000 && Property<string>(result, "Stderr").Length == 4000 && Property<bool>(result, "Truncated"),
            "B24 both retained prefixes are strictly bounded with explicit truncation");
        var timedOut = await ShellRunAsync(Child("--review-shell-block"), TimeSpan.FromSeconds(1), CancellationToken.None);
        check(Property<string?>(timedOut, "Failure") == "command_timeout" && Property<int?>(timedOut, "ExitCode") is null,
            "B24 command timeout is failure rather than fabricated exit");
        if (int.TryParse(Property<string>(timedOut, "Stdout").Trim(), out var pid))
            check(HasExited(pid), "B24 timeout child process is cleaned up");
        else check(false, "B24 timeout fixture reached process barrier");
        check(IsTerminatedLinuxStat("10001 (synthetic (child)) Z 1 0") && IsTerminatedLinuxStat("10001 (synthetic) X 1 0"),
            "B24 Linux zombie/dead task is terminated even before reaping");
        check(!IsTerminatedLinuxStat("10001 (synthetic) S 1 0") && !IsTerminatedLinuxStat("10001 (synthetic) R 1 0")
            && !IsTerminatedLinuxStat("10001 (synthetic) D 1 0") && !IsTerminatedLinuxStat("10001 (synthetic) T 1 0")
            && !IsTerminatedLinuxStat("malformed"), "B24 runnable blocked stopped or malformed Linux task is not terminated");
        await CancellationBarrierAsync(check);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var propagated = false;
        try { await ShellRunAsync(Child("--review-shell-block"), TimeSpan.FromSeconds(10), cancelled.Token); }
        catch (OperationCanceledException) { propagated = true; }
        check(propagated, "B24 pre-start caller cancellation is propagated");
        var missing = new ProcessStartInfo("botagent-synthetic-missing-command") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        var startFailure = await ShellRunAsync(missing, TimeSpan.FromSeconds(1), CancellationToken.None);
        check(Property<string?>(startFailure, "Failure")?.StartsWith("process_start_failed:", StringComparison.Ordinal) == true,
            "B24 start failure has stable failure semantics");
    }

    private static async Task CancellationBarrierAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-review-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "synthetic-ready");
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(root, "synthetic-ready");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => ready.TrySetResult();
        watcher.Renamed += (_, _) => ready.TrySetResult();
        watcher.EnableRaisingEvents = true;
        var start = Child("--review-shell-tree");
        start.ArgumentList.Add(marker);
        var pending = ShellRunAsync(start, TimeSpan.FromSeconds(10), cancellation.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The child publishes the closed marker with an atomic rename before cancellation.
            var ids = await File.ReadAllTextAsync(marker);
            check(ids.Split(',').Length == 2, "B24 process-tree cancellation fixture reaches deterministic barrier");
            cancellation.Cancel();
            var cancelled = false;
            try { await pending; }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "B24 in-flight caller cancellation is propagated after readers settle");
            var pids = ids.Split(',').Select(int.Parse).ToArray();
            var allExited = pids.All(HasExited);
            if (!allExited && OperatingSystem.IsLinux())
                foreach (var pid in pids)
                {
                    try
                    {
                        var stat = File.ReadAllText($"/proc/{pid}/stat");
                        var end = stat.LastIndexOf(')');
                        Console.WriteLine($"B24 synthetic liveness pid={pid} state={(end >= 0 && end + 2 < stat.Length ? stat[end + 2] : '?')}");
                    }
                    catch (Exception ex) { Console.WriteLine($"B24 synthetic liveness pid={pid} read={ex.GetType().Name}"); }
                }
            check(allExited, "B24 caller cancellation cleans parent and descendant process");
        }
        finally
        {
            cancellation.Cancel();
            try { await pending; } catch (OperationCanceledException) { }
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(root).StartsWith("botagent-review-shell-", StringComparison.Ordinal))
                throw new InvalidOperationException("unsafe_synthetic_shell_cleanup");
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool HasExited(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            try { return IsTerminatedLinuxStat(File.ReadAllText($"/proc/{pid}/stat")); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    private static bool IsTerminatedLinuxStat(string stat)
    {
        // /proc retains a killed orphan as Z until its reaper collects it; it cannot execute.
        // comm may contain spaces and parentheses, so state follows the final closing parenthesis.
        var end = stat.LastIndexOf(')');
        return end >= 0 && end + 3 < stat.Length && stat[end + 1] == ' '
            && stat[end + 3] == ' ' && stat[end + 2] is 'Z' or 'X';
    }
}

public class AgentStorageSessionProxy : DispatchProxy
{
    public string? FailAt { get; set; }
    public int Finished { get; private set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == FailAt) throw new InvalidOperationException("synthetic_session_failure");
        return method.Name switch
        {
            "EnsureCurrent" => new AgentSession { Id = "synthetic-session", Name = "synthetic", Backend = "server", AutoNamed = false },
            "History" => new List<(string, string)>(),
            "StartRun" => "synthetic-run",
            "FinishRun" => Finish(),
            _ => throw new InvalidOperationException("unexpected_synthetic_session_call:" + method.Name)
        };
    }
    private object? Finish() { Finished++; return null; }
}
