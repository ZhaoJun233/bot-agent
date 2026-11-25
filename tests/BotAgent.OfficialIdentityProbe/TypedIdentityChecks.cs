using System.Security.Cryptography;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;

internal static class TypedIdentityChecks
{
    private static readonly string Evidence = new('a', 64);
    private static OfficialIdentityKey Key(OfficialIdentityKind kind, string raw = "same", string app = "app-A", string target = "group-A",
        OfficialMessageDirection direction = OfficialMessageDirection.None, bool group = true)
        => new(new(app, kind, kind is OfficialIdentityKind.GroupMember or OfficialIdentityKind.BotSelf or OfficialIdentityKind.Message
            ? new ConversationId(PlatformId.QqOfficial, app, group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, target) : null,
            direction), raw);
    private static void Require(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }
    private static void Refused(Action action, string reason)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { return; }
        throw new InvalidOperationException(reason);
    }
    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        async Task Check(string name, Action body) => await test("typed: " + name, () => { body(); return Task.CompletedTask; });

        await Check("application, type, group and direction remain distinct after restart", () =>
        {
            using var temp = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(temp.Map);
            var keys = new[] {
                Key(OfficialIdentityKind.Group), Key(OfficialIdentityKind.C2cUser), Key(OfficialIdentityKind.GroupMember),
                Key(OfficialIdentityKind.GroupMember, target: "group-B"), Key(OfficialIdentityKind.GroupMember, app: "app-B"),
                Key(OfficialIdentityKind.BotSelf), Key(OfficialIdentityKind.Message, direction: OfficialMessageDirection.Inbound),
                Key(OfficialIdentityKind.Message, direction: OfficialMessageDirection.Outbound),
                Key(OfficialIdentityKind.Message, direction: OfficialMessageDirection.Inbound, group: false) };
            var aliases = keys.Select(ids.AliasFor).ToArray();
            Require(aliases.Distinct().Count() == keys.Length, "identity domains merged");
            var reopened = new OfficialIdentityMap(temp.Map);
            for (var i = 0; i < keys.Length; i++)
                Require(reopened.AliasFor(keys[i]) == aliases[i] && reopened.OriginalOf(aliases[i], keys[i].Scope) == keys[i].NativeId
                    && (long)(double)aliases[i] == aliases[i], "identity changed or lost numeric precision");
            Require(reopened.LegacyCount == 0 && reopened.Count == keys.Length, "index not committed with aliases");
        });

        await Check("reverse lookup cannot cross an app, type, group or direction", () =>
        {
            using var temp = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(temp.Map);
            var own = Key(OfficialIdentityKind.Message, direction: OfficialMessageDirection.Inbound);
            var alias = ids.AliasFor(own);
            var other = new[] {
                Key(OfficialIdentityKind.Message, app: "app-B", direction: OfficialMessageDirection.Inbound),
                Key(OfficialIdentityKind.Message, target: "group-B", direction: OfficialMessageDirection.Inbound),
                Key(OfficialIdentityKind.Message, direction: OfficialMessageDirection.Outbound), Key(OfficialIdentityKind.Group) };
            Require(other.All(key => ids.OriginalOf(alias, key.Scope) is null && !ids.TryGetAlias(key, out _)), "reverse lookup bypassed scope");
        });

        await Check("opaque case, separators and Unicode round-trip without merging", () =>
        {
            using var temp = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(temp.Map);
            var raw = new[] { "same", "Same", "a/b", "a:b", "a\"b", "身份", "emoji-😀" };
            var keys = raw.Select(value => Key(OfficialIdentityKind.C2cUser, value)).ToArray();
            var aliases = keys.Select(ids.AliasFor).ToArray();
            var reopened = new OfficialIdentityMap(temp.Map);
            Require(aliases.Distinct().Count() == keys.Length && keys.Select((key, i) => reopened.OriginalOf(aliases[i], key.Scope) == raw[i]).All(x => x), "opaque value changed");
        });

        await Check("invalid identity contexts fail before storage", () =>
        {
            foreach (var raw in new[] { "", " ", "a b", "a\nb", "\ud800" })
                Refused(() => Key(OfficialIdentityKind.Group, raw), "malformed ID accepted");
            Refused(() => new OfficialIdentityScope("app-A", OfficialIdentityKind.GroupMember), "member without group accepted");
            Refused(() => Key(OfficialIdentityKind.GroupMember, group: false), "member in C2C scope accepted");
            Refused(() => Key(OfficialIdentityKind.Message), "message without direction accepted");
            Refused(() => Key(OfficialIdentityKind.Group, direction: OfficialMessageDirection.Outbound), "non-message direction accepted");
            Refused(() => new OfficialIdentityScope("app-A", OfficialIdentityKind.Message,
                new(PlatformId.QqOfficial, "app-B", ConversationKind.GroupChat, "group-A"), OfficialMessageDirection.Inbound), "foreign account scope accepted");
        });

        await Check("unverified legacy entries stay reserved without current-AppID attribution", () =>
        {
            using var temp = new SyntheticDirectory();
            var old = new OfficialIdMap(temp.Map);
            var prior = old.AliasFor("same");
            var before = Hash(temp.Map);
            var ids = new OfficialIdentityMap(temp.Map);
            var key = Key(OfficialIdentityKind.Group);
            Require(ids.LegacyCount == 1 && ids.OriginalOf(prior, key.Scope) is null && !ids.TryGetAlias(key, out _), "legacy falsely trusted");
            Refused(() => ids.AliasFor(key), "legacy silently assigned an app");
            Require(Hash(temp.Map) == before, "refusal changed legacy bytes");
            var fresh = ids.AliasFor(Key(OfficialIdentityKind.Group, "fresh"));
            Require(fresh != prior && ids.LegacyCount == 1, "legacy reservation reused");
        });

        await Check("verified batch preserves aliases, keys and evidence through reopen", () =>
        {
            using var temp = new SyntheticDirectory();
            var old = new OfficialIdMap(temp.Map);
            var group = old.AliasFor("group-original");
            var user = old.AliasFor("user-original");
            old.AliasFor("unverified");
            var ids = new OfficialIdentityMap(temp.Map);
            var groupKey = Key(OfficialIdentityKind.Group, "group-original");
            var userKey = Key(OfficialIdentityKind.GroupMember, "user-original", target: "group-original");
            ids.ImportVerifiedLegacy(Hash(temp.Map), [new(groupKey, group, Evidence), new(userKey, user, Evidence)]);
            var reopened = new OfficialIdentityMap(temp.Map);
            Require(reopened.AliasFor(groupKey) == group && reopened.AliasFor(userKey) == user && reopened.LegacyCount == 1, "migration renumbered aliases");
            var snapshot = Hash(temp.Map);
            reopened.ImportVerifiedLegacy(snapshot, [new(groupKey, group, Evidence), new(userKey, user, Evidence)]);
            Require(Hash(temp.Map) == snapshot, "identical migration is not idempotent");
            Require(reopened.AliasFor(Key(OfficialIdentityKind.Group, "group-original", app: "app-B")) != group, "verified owner merged with new app");
        });

        await Check("stale preview, bad evidence, mismatched aliases and batch conflicts change nothing", () =>
        {
            using var temp = new SyntheticDirectory();
            var old = new OfficialIdMap(temp.Map);
            var first = old.AliasFor("first");
            var second = old.AliasFor("second");
            var ids = new OfficialIdentityMap(temp.Map);
            var hash = Hash(temp.Map);
            var key = Key(OfficialIdentityKind.Group, "first");
            var valid = new OfficialLegacyBinding(key, first, Evidence);
            Refused(() => ids.ImportVerifiedLegacy(new('b', 64), [valid]), "stale snapshot accepted");
            Refused(() => ids.ImportVerifiedLegacy(hash, [valid with { EvidenceSha256 = "" }]), "missing evidence accepted");
            Refused(() => ids.ImportVerifiedLegacy(hash, [valid, new(Key(OfficialIdentityKind.Group, "missing"), second, Evidence)]), "partial batch accepted");
            Refused(() => ids.ImportVerifiedLegacy(hash, [valid, valid]), "duplicate claim accepted");
            Refused(() => ids.ImportVerifiedLegacy(hash, [valid, new(Key(OfficialIdentityKind.C2cUser, "first"), first, Evidence)]), "alias assigned two types");
            Require(ids.LegacyCount == 2 && !ids.TryGetAlias(key, out _) && Hash(temp.Map) == hash, "failed migration left labels");
        });

        await Check("metadata publication failure rolls back allocation and migration in memory", () =>
        {
            using var temp = new SyntheticDirectory();
            var prior = new OfficialIdMap(temp.Map).AliasFor("legacy");
            var bytes = File.ReadAllBytes(temp.Map);
            var ids = new OfficialIdentityMap(temp.Map);
            var hash = Hash(temp.Map);
            var key = Key(OfficialIdentityKind.Group, "legacy");
            File.Delete(temp.Map);
            Directory.CreateDirectory(temp.Map);
            Refused(() => ids.ImportVerifiedLegacy(hash, [new(key, prior, Evidence)]), "migration swallowed publication failure");
            Refused(() => ids.AliasFor(Key(OfficialIdentityKind.C2cUser, "fresh")), "allocation swallowed publication failure");
            Require(ids.LegacyCount == 1 && ids.Count == 1 && !ids.TryGetAlias(key, out _), "failed publish survived in memory");
            Directory.Delete(temp.Map);
            File.WriteAllBytes(temp.Map, bytes);
            ids.ImportVerifiedLegacy(hash, [new(key, prior, Evidence)]);
            Require(new OfficialIdentityMap(temp.Map).AliasFor(key) == prior, "explicit retry failed");
        });

        await Check("downgrade keeps all old and newly allocated aliases", () =>
        {
            using var temp = new SyntheticDirectory();
            var prior = new OfficialIdMap(temp.Map).AliasFor("legacy");
            var ids = new OfficialIdentityMap(temp.Map);
            var fresh = ids.AliasFor(Key(OfficialIdentityKind.Group, "fresh"));
            var exported = Path.Combine(temp.Root, "downgrade.json");
            File.WriteAllText(exported, ids.ExportLegacySnapshot());
            var old = new OfficialIdMap(exported);
            Require(old.Count == 2 && old.OriginalOf(prior) == "legacy" && old.OriginalOf(fresh) == "fresh", "downgrade dropped a binding");
            Refused(() => new OfficialIdMap(temp.Map), "old runtime accepted typed metadata");
        });

        await Check("ambiguous downgrade refuses instead of dropping one identity", () =>
        {
            using var temp = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(temp.Map);
            ids.AliasFor(Key(OfficialIdentityKind.Group));
            ids.AliasFor(Key(OfficialIdentityKind.C2cUser));
            var before = Hash(temp.Map);
            Refused(() => ids.ExportLegacySnapshot(), "ambiguous downgrade emitted a legacy map");
            Require(Hash(temp.Map) == before && new OfficialIdentityMap(temp.Map).Count == 2, "downgrade changed source");
        });

        var corruptions = new (string Name, Action<JsonObject> Mutate)[] {
            ("version", root => root["officialIdentities"]!["version"] = 2),
            ("unknown-field", root => root["officialIdentities"]!["extra"] = true),
            ("unknown-entry-field", root => Entry(root)["extra"] = true),
            ("null-key", root => Entry(root)["key"] = null),
            ("bad-key", root => Entry(root)["key"] = "not-a-key"),
            ("dangling-alias", root => Entry(root)["alias"] = Channels.AliasBase - 1),
            ("duplicate-entry", root => ((JsonArray)root["officialIdentities"]!["bindings"]!).Add(Entry(root).DeepClone())),
            ("bad-evidence", root => Entry(root)["evidenceSha256"] = "not-a-digest"),
            ("false-legacy", root => Entry(root)["evidenceSha256"] = Evidence),
            ("missing-property", root => Entry(root).Remove("evidenceSha256")) };
        foreach (var (name, mutate) in corruptions)
            await Check("corrupt index " + name + " is preserved and refused", () =>
            {
                using var temp = new SyntheticDirectory();
                new OfficialIdentityMap(temp.Map).AliasFor(Key(OfficialIdentityKind.Group));
                var doc = JsonNode.Parse(File.ReadAllText(temp.Map))!.AsObject();
                mutate(doc);
                File.WriteAllText(temp.Map, doc.ToJsonString());
                var before = Hash(temp.Map);
                Refused(() => new OfficialIdentityMap(temp.Map), "corrupt index accepted");
                Require(Hash(temp.Map) == before, "corrupt index overwritten");
            });

        await Check("concurrent typed allocations and reopen preserve the whole index", () =>
        {
            using var temp = new SyntheticDirectory();
            var ids = new OfficialIdentityMap(temp.Map);
            var keys = Enumerable.Range(0, 32).Select(i => Key(OfficialIdentityKind.C2cUser, "subject-" + i)).ToArray();
            Parallel.ForEach(keys, key => ids.AliasFor(key));
            var reopened = new OfficialIdentityMap(temp.Map);
            Require(reopened.Count == keys.Length && reopened.LegacyCount == 0 && keys.All(key => reopened.TryGetAlias(key, out _)), "concurrent index entry lost");
        });

        await Check("BOM compatibility and migration digest use exact source bytes", () =>
        {
            using var temp = new SyntheticDirectory();
            foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
            {
                File.WriteAllText(temp.Map, JsonSerializer.Serialize(new { map = new Dictionary<string, long> { ["legacy"] = Channels.AliasBase + 17 } }), encoding);
                var ids = new OfficialIdentityMap(temp.Map);
                Require(ids.SnapshotSha256 == Hash(temp.Map), "digest did not bind original bytes");
                ids.ImportVerifiedLegacy(Hash(temp.Map), [new(Key(OfficialIdentityKind.Group, "legacy"), Channels.AliasBase + 17, Evidence)]);
                Require(new OfficialIdentityMap(temp.Map).LegacyCount == 0, "BOM migration failed");
            }
        });

        await test("typed: hard process termination retains both aliases and their type index", async () =>
        {
            using var temp = new SyntheticDirectory();
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--allocate-typed-child");
            start.ArgumentList.Add(temp.Map);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("child_not_started");
            try
            {
                Require(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) == "TYPED_ALLOCATED", "child did not complete allocations");
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                var reopened = new OfficialIdentityMap(temp.Map);
                Require(reopened.Count == 2 && reopened.LegacyCount == 0
                    && reopened.TryGetAlias(Key(OfficialIdentityKind.Group), out var group)
                    && reopened.TryGetAlias(Key(OfficialIdentityKind.C2cUser), out var user) && group != user, "crash lost a committed identity index");
            }
            finally
            {
                if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            }
        });
    }

    private static JsonObject Entry(JsonObject root) => root["officialIdentities"]!["bindings"]![0]!.AsObject();
}
