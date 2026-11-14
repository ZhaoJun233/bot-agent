using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Platforms;

PlatformClock.Initialize(new FrozenClock());
if (args is ["--ledger-child", var ledgerPath, var ledgerPhase])
{
    await DeliveryLedgerChecks.ChildAsync(ledgerPath, ledgerPhase);
    return 0;
}
if (args is ["--allocate-typed-child", var typedChildPath])
{
    var ids = new OfficialIdentityMap(typedChildPath);
    ids.AliasFor(new(new("app-A", OfficialIdentityKind.Group), "same"));
    ids.AliasFor(new(new("app-A", OfficialIdentityKind.C2cUser), "same"));
    Console.WriteLine("TYPED_ALLOCATED");
    await Task.Delay(Timeout.Infinite);
    return 0;
}
if (args is ["--allocate-child", var childPath])
{
    var childMap = new OfficialIdMap(childPath);
    childMap.AliasFor("synthetic-first");
    childMap.AliasFor("synthetic-second");
    Console.WriteLine("ALLOCATED");
    await Task.Delay(Timeout.Infinite);
    return 0;
}

var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error)
    {
        failed++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message);
        var frames = new System.Diagnostics.StackTrace(error, false).GetFrames() ?? [];
        Console.WriteLine("FAIL_STACK " + name + " hresult=" + error.HResult + " methods="
            + string.Join(",", frames.Select(frame => frame.GetMethod()).Select(method => method?.DeclaringType?.FullName + "." + method?.Name)));
    }
}
void Require(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
}
bool Refused(Action action)
{
    try { action(); return false; }
    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException)
    { return true; }
}
if (args is ["--windows-recovery-only"])
{
    await WindowsRecoveryChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--messaging-capabilities-only"])
{
    await MessagingCapabilityChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--authorization-only"])
{
    await RuntimeDeliveryChecks.RunAuthorizationAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--own-recall-only"])
{
    await RuntimeDeliveryChecks.RunOwnRecallAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--crash-only"])
{
    await DeliveryLedgerChecks.RunCrashRecoveryAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--tts-download-only"])
{
    await RuntimeDeliveryChecks.RunTtsDownloadAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--inbound-media-only"])
{
    await RuntimeDeliveryChecks.RunInboundMediaAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--content-only"])
{
    await RuntimeDeliveryChecks.RunContentAsync(Test, (FrozenClock)PlatformClock.Current);
    await MusicReceiptChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--dedup-only"])
{
    await RuntimeDeliveryChecks.RunDedupAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--indexed-state-only"])
{
    await DeliveryLedgerChecks.RunIndexedAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--relationships-only"])
{
    await RuntimeDeliveryChecks.RunRelationshipsAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--references-only"])
{
    await RuntimeDeliveryChecks.RunReferencesAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--runtime-only"])
{
    await RuntimeDeliveryChecks.RunAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--host-media-only"])
{
    await HostMediaChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--binding-only"])
{
    await ReplyBindingChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--ledger-only"])
{
    await DeliveryLedgerChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--connection-only"])
{
    await ConnectionChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--roles-only"])
{
    await MemberRoleChecks.RunAsync(Test, (FrozenClock)PlatformClock.Current);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
if (args is ["--api-only"])
{
    await ApiResultChecks.RunAsync(Test);
    Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
    return failed == 0 ? 0 : 1;
}
string Document(params (string Raw, long Alias)[] entries)
    => JsonSerializer.Serialize(new { aliasBase = Channels.AliasBase, map = entries.ToDictionary(x => x.Raw, x => x.Alias) });

await Test("each returned binding survives reopen within one clock tick", () =>
{
    using var temp = new SyntheticDirectory();
    var ids = new OfficialIdMap(temp.Map);
    var first = ids.AliasFor("synthetic-first");
    var second = ids.AliasFor("synthetic-second");
    var reopened = new OfficialIdMap(temp.Map);
    Require(reopened.OriginalOf(first) == "synthetic-first" && reopened.OriginalOf(second) == "synthetic-second", "returned binding not durable");
    return Task.CompletedTask;
});

await Test("legacy allocator and already probed aliases stay unchanged", () =>
{
    using var temp = new SyntheticDirectory();
    const string raw = "synthetic-known";
    var expected = Channels.AliasBase + BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0);
    var existing = expected + 17;
    File.WriteAllText(temp.Map, Document((raw, existing)));
    var ids = new OfficialIdMap(temp.Map);
    Require(ids.AliasFor(raw) == existing, "legacy alias was recomputed");
    var otherExpected = Channels.AliasBase + BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-new")), 0);
    Require(ids.AliasFor("synthetic-new") == otherExpected && new OfficialIdMap(temp.Map).OriginalOf(existing) == raw, "allocation rule or old identity changed");
    return Task.CompletedTask;
});

await Test("legacy hash collision probes while preserving prior owner", () =>
{
    using var temp = new SyntheticDirectory();
    var candidate = Channels.AliasBase + BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-collision")), 0);
    File.WriteAllText(temp.Map, Document(("synthetic-prior-owner", candidate)));
    var ids = new OfficialIdMap(temp.Map);
    Require(ids.AliasFor("synthetic-collision") == candidate + 1 && ids.OriginalOf(candidate) == "synthetic-prior-owner", "collision overwrote legacy owner");
    return Task.CompletedTask;
});

await Test("parallel allocations are unique and all are persisted", async () =>
{
    using var temp = new SyntheticDirectory();
    var ids = new OfficialIdMap(temp.Map);
    var values = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => ids.AliasFor("synthetic-parallel-" + i))));
    var reopened = new OfficialIdMap(temp.Map);
    Require(values.Distinct().Count() == 64 && reopened.Count == 64, "parallel binding lost or duplicated");
    for (var i = 0; i < values.Length; i++) Require(reopened.OriginalOf(values[i]) == "synthetic-parallel-" + i, "parallel owner changed");
});

await Test("failed publication never exposes the rejected binding", () =>
{
    using var temp = new SyntheticDirectory();
    var prior = Channels.AliasBase + 42;
    File.WriteAllText(temp.Map, Document(("synthetic-retained", prior)));
    var ids = new OfficialIdMap(temp.Map);
    var backup = temp.Map + ".saved";
    File.Move(temp.Map, backup);
    Directory.CreateDirectory(temp.Map);
    Require(Refused(() => ids.AliasFor("synthetic-rejected")), "write error was swallowed");
    Require(ids.Count == 1 && !ids.TryGetAlias("synthetic-rejected", out _) && ids.OriginalOf(prior) == "synthetic-retained", "rejected binding escaped into memory");
    Directory.Delete(temp.Map);
    File.Move(backup, temp.Map);
    ids.Flush();
    Require(new OfficialIdMap(temp.Map).Count == 1, "Flush committed a rejected binding");
    var retry = ids.AliasFor("synthetic-rejected");
    Require(new OfficialIdMap(temp.Map).OriginalOf(retry) == "synthetic-rejected", "explicit retry after repair failed");
    return Task.CompletedTask;
});

await Test("unwritable parent cannot allocate an in-memory-only identity", () =>
{
    using var temp = new SyntheticDirectory();
    var parent = Path.Combine(temp.Root, "occupied");
    File.WriteAllText(parent, "synthetic-file");
    var ids = new OfficialIdMap(Path.Combine(parent, "map.json"));
    Require(Refused(() => ids.AliasFor("synthetic-unwritable")) && ids.Count == 0, "unwritable store returned an alias");
    return Task.CompletedTask;
});

var invalid = new (string Name, string Json)[]
{
    ("truncated JSON", "{\"map\":"),
    ("empty document", ""),
    ("missing map", "{}"),
    ("null map", "{\"map\":null}"),
    ("array map", "{\"map\":[]}"),
    ("null alias", "{\"map\":{\"synthetic-a\":null}}"),
    ("string alias", "{\"map\":{\"synthetic-a\":\"8000000000000001\"}}"),
    ("fraction alias", "{\"map\":{\"synthetic-a\":8000000000000001.5}}"),
    ("below range", Document(("synthetic-a", Channels.AliasBase - 1))),
    ("above range", Document(("synthetic-a", Channels.AliasBase + 2_000_000_000_000L))),
    ("duplicate alias", Document(("synthetic-a", Channels.AliasBase + 1), ("synthetic-b", Channels.AliasBase + 1))),
    ("duplicate raw", "{\"map\":{\"synthetic-a\":8000000000000001,\"synthetic-a\":8000000000000002}}"),
    ("duplicate root map", "{\"map\":{},\"map\":{\"synthetic-a\":8000000000000001}}"),
    ("wrong range header", "{\"aliasBase\":10001,\"map\":{}}"),
    ("unknown schema", "{\"schemaVersion\":99,\"map\":{}}"),
    ("empty raw", Document(("", Channels.AliasBase + 1))),
};
foreach (var item in invalid)
{
    await Test("reject and preserve " + item.Name, () =>
    {
        using var temp = new SyntheticDirectory();
        File.WriteAllText(temp.Map, item.Json);
        var before = File.ReadAllBytes(temp.Map);
        Require(Refused(() => _ = new OfficialIdMap(temp.Map)), "damaged map was accepted");
        Require(before.AsSpan().SequenceEqual(File.ReadAllBytes(temp.Map)), "damaged evidence was rewritten");
        return Task.CompletedTask;
    });
}

await Test("directory at map path is an error, never an empty table", () =>
{
    using var temp = new SyntheticDirectory();
    Directory.CreateDirectory(temp.Map);
    Require(Refused(() => _ = new OfficialIdMap(temp.Map)), "directory treated as missing file");
    return Task.CompletedTask;
});

await Test("old map-only and empty documents can be extended", () =>
{
    using var temp = new SyntheticDirectory();
    File.WriteAllText(temp.Map, "{\"map\":{}}");
    var ids = new OfficialIdMap(temp.Map);
    var alias = ids.AliasFor("synthetic-first");
    Require(new OfficialIdMap(temp.Map).OriginalOf(alias) == "synthetic-first", "valid legacy shape rejected");
    return Task.CompletedTask;
});

await Test("orphan temporary is neither trusted nor overwritten", () =>
{
    using var temp = new SyntheticDirectory();
    var orphan = temp.Map + ".tmp";
    File.WriteAllText(orphan, "synthetic-orphan");
    var ids = new OfficialIdMap(temp.Map);
    Require(ids.Count == 0, "orphan imported");
    ids.AliasFor("synthetic-first");
    Require(File.ReadAllText(orphan) == "synthetic-orphan", "orphan evidence overwritten");
    Require(Directory.GetFiles(temp.Root, "*.tmp").Length == 1, "new temporary leaked");
    return Task.CompletedTask;
});

await Test("all default aliases round-trip through a JSON double", () =>
{
    using var temp = new SyntheticDirectory();
    var ids = new OfficialIdMap(temp.Map);
    foreach (var raw in new[] { "synthetic-a", "synthetic-A", "synthetic-other" })
    {
        var alias = ids.AliasFor(raw);
        var number = JsonSerializer.Deserialize<double>(JsonSerializer.Serialize(alias));
        Require(alias <= 9_007_199_254_740_991L && checked((long)number) == alias, "alias cannot round-trip through JavaScript Number");
    }
    Require(ids.Count == 3, "case-sensitive native keys were merged");
    return Task.CompletedTask;
});

await Test("legacy small range exhausts without replacing an owner", () =>
{
    var ids = new OfficialIdMap(null, 10001, 10002, durable: false);
    Require(ids.AliasFor("synthetic-first") == 10001, "allocation escaped configured range");
    Require(Refused(() => ids.AliasFor("synthetic-second")) && ids.Count == 1 && ids.OriginalOf(10001) == "synthetic-first", "exhausted range reused an identity");
    return Task.CompletedTask;
});

await Test("strict hash collision continues to reject instead of probing", () =>
{
    var ids = new OfficialIdMap(null, 10001, 10002, durable: true);
    Require(ids.AliasFor("synthetic-first") == 10001, "strict candidate changed");
    Require(Refused(() => ids.AliasFor("synthetic-second")) && ids.OriginalOf(10001) == "synthetic-first", "strict collision was reassigned");
    return Task.CompletedTask;
});

await Test("hard process termination preserves every returned binding without Flush", async () =>
{
    using var temp = new SyntheticDirectory();
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--allocate-child");
    start.ArgumentList.Add(temp.Map);
    using var child = Process.Start(start) ?? throw new InvalidOperationException("child_not_started");
    try
    {
        var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Require(line == "ALLOCATED", "child did not return from both allocations");
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var reopened = new OfficialIdMap(temp.Map);
        Require(reopened.Count == 2 && reopened.TryGetAlias("synthetic-first", out _) && reopened.TryGetAlias("synthetic-second", out _), "process death lost an acknowledged identity");
    }
    finally
    {
        if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
    }
});

await TypedIdentityChecks.RunAsync(Test);
if (OperatingSystem.IsWindows()) await WindowsRecoveryChecks.RunAsync(Test);
await MigrationCommandChecks.RunAsync(Test);
await GatewayIdentityChecks.RunAsync(Test);
await SelfIdentityChecks.RunAsync(Test, (FrozenClock)PlatformClock.Current);
await ConnectionChecks.RunAsync(Test);
await MemberRoleChecks.RunAsync(Test, (FrozenClock)PlatformClock.Current);
await ApiResultChecks.RunAsync(Test);
await DeliveryLedgerChecks.RunAsync(Test);
await ReplyBindingChecks.RunAsync(Test);
await RuntimeDeliveryChecks.RunAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunReferencesAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunRelationshipsAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunDedupAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunContentAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunInboundMediaAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunTtsDownloadAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunOwnRecallAsync(Test, (FrozenClock)PlatformClock.Current);
await RuntimeDeliveryChecks.RunAuthorizationAsync(Test, (FrozenClock)PlatformClock.Current);
await MusicReceiptChecks.RunAsync(Test);
await DeliveryLedgerChecks.RunIndexedAsync(Test);
await HostMediaChecks.RunAsync(Test);
await MessagingCapabilityChecks.RunAsync(Test);
Console.WriteLine($"SUMMARY passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;

sealed class FrozenClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => Now;
    public DateTime LocalDateTime => Now.DateTime;
    public long TickCount => 0;
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
    public Task Delay(int milliseconds, CancellationToken ct = default) => Task.Delay(milliseconds, ct);
}

sealed class SyntheticDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "botagent-official-map-" + Guid.NewGuid().ToString("N"));
    public string Map => Path.Combine(Root, "map.json");
    public SyntheticDirectory() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !Path.GetFileName(resolved).StartsWith("botagent-official-map-", StringComparison.Ordinal))
            throw new InvalidOperationException("unsafe_synthetic_cleanup");
        Directory.Delete(resolved, recursive: true);
    }
}
