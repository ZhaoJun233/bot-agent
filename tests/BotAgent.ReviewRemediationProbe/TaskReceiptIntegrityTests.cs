using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Panel;

namespace BotAgent.ReviewRemediationProbe;

internal static class TaskReceiptIntegrityTests
{
    private static readonly Type StoreType = typeof(WebUiServer).Assembly.GetType(
        "BotAgent.Adapters.Persistence.ZbaTaskReceiptStore", throwOnError: true)!;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

    internal static Task RunAsync(Action<bool, string> check)
    {
        Run(check);
        return Task.CompletedTask;
    }

    private static object Store(string path, List<string> logs)
        => Activator.CreateInstance(StoreType, Instance, binder: null,
            args: [path, (Func<DateTimeOffset>)(() => Now), (Action<string>)logs.Add],
            culture: CultureInfo.InvariantCulture)!;

    private static bool Add(object store, string key = "synthetic-new-key")
        => (bool)StoreType.GetMethod("TryAdd", Instance)!.Invoke(store,
            [key, "synthetic-new-task", "synthetic-instance", Now])!;

    private static object? Find(object store, string key = "synthetic-old-key")
        => StoreType.GetMethod("Find", Instance)!.Invoke(store, [key]);

    private static JsonObject Entry(DateTimeOffset? acceptedAt = null) => new()
    {
        ["keyHash"] = "synthetic-old-key", ["taskId"] = "synthetic-old-task",
        ["instanceId"] = "synthetic-old-instance", ["acceptedAt"] = (acceptedAt ?? Now).ToString("O")
    };

    private static string Document(params JsonNode?[] entries)
        => new JsonObject { ["version"] = 1, ["receipts"] = new JsonArray(entries) }.ToJsonString();

    private static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-receipt-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cases = new Dictionary<string, string>
            {
                ["invalid-json"] = "not-json-synthetic-private-marker",
                ["empty-file"] = "",
                ["null-root"] = "null",
                ["missing-array"] = "{}",
                ["wrong-array"] = "{\"version\":1,\"receipts\":{}}",
                ["future-version"] = "{\"version\":2,\"receipts\":[]}",
                ["null-entry"] = Document((JsonNode?)null),
                ["missing-field"] = Document(new JsonObject { ["keyHash"] = "synthetic-old-key" }),
                ["mixed-valid-invalid"] = Document(Entry(), null),
                ["duplicate-key"] = Document(Entry(), Entry()),
            };
            var wrongType = Entry();
            wrongType["taskId"] = 42;
            cases["wrong-field-type"] = Document(wrongType);
            var wrongDate = Entry();
            wrongDate["acceptedAt"] = "not-a-date";
            cases["invalid-date"] = Document(wrongDate);
            foreach (var (name, contents) in cases)
            {
                var path = Path.Combine(root, name + ".json");
                File.WriteAllText(path, contents);
                var logs = new List<string>();
                var store = Store(path, logs);
                check(Find(store) is null && !Add(store), name + ": unknown history blocks admission");
                check(File.ReadAllText(path) == contents, name + ": original file preserved");
                check(logs.Count > 0 && logs.All(line => !line.Contains("synthetic-private-marker", StringComparison.Ordinal)),
                    name + ": safe diagnostic without payload");
            }

            var freshPath = Path.Combine(root, "fresh.json");
            check(Add(Store(freshPath, [])), "missing file permits first admission");
            var validPath = Path.Combine(root, "valid.json");
            File.WriteAllText(validPath, Document(Entry()));
            var valid = Store(validPath, []);
            check(Find(valid) is not null && Add(valid), "valid old history survives new admission");
            check(Find(Store(validPath, [])) is not null, "new store can reload persisted old key");

            var emptyPath = Path.Combine(root, "empty.json");
            File.WriteAllText(emptyPath, Document());
            check(Add(Store(emptyPath, [])), "valid empty history permits admission");
            var expiredPath = Path.Combine(root, "expired.json");
            File.WriteAllText(expiredPath, Document(Entry(Now.AddDays(-8))));
            var expired = Store(expiredPath, []);
            check(Find(expired) is null && Add(expired), "valid expired history is pruned and stays writable");

            var recoveredPath = Path.Combine(root, "recovered.json");
            File.WriteAllText(recoveredPath, "not-json");
            var unavailable = Store(recoveredPath, []);
            File.WriteAllText(recoveredPath, Document(Entry()));
            check(!Add(unavailable) && File.ReadAllText(recoveredPath) == Document(Entry()),
                "repair does not re-enable an instance with unverified history");
            check(Find(Store(recoveredPath, [])) is not null, "new instance verifies repaired history");

            var lockedPath = Path.Combine(root, "locked.json");
            File.WriteAllText(lockedPath, Document(Entry()));
            object locked;
            using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                locked = Store(lockedPath, []);
            check(!Add(locked) && File.ReadAllText(lockedPath) == Document(Entry()),
                "read failure remains closed after file becomes writable");
            check(Find(Store(lockedPath, [])) is not null, "new instance recovers from read failure");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
