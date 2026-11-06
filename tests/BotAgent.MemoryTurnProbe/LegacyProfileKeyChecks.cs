using System.Globalization;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Services.Memory;
using BotAgent.Services.Reply;

namespace BotAgent.MemoryTurnProbe;

internal static class LegacyProfileKeyChecks
{
    private static readonly DriverKey Driver = new("qq.private", "legacy", "");
    private static readonly ParticipantId Subject = new(Driver.PlatformId, Driver.AccountScope, "10002");
    private static readonly ConversationId Group = new(Driver.PlatformId, Driver.AccountScope, ConversationKind.GroupChat, "10004");
    private const string ExactKey = "legacy-profile:v1:D65F1B6124E01775289C81074F1BE3436D7ACC0A625097EFAB8E1B9F1093D16A";

    public static void Run()
    {
        VectorsAndIsolation();
        EncodingAndFormat();
        InvalidInputs();
        GetterBoundary();
    }

    private static void VectorsAndIsolation()
    {
        string? Key(DriverKey driver) => LegacyProfileScopeKey.TryCreate(driver,
            Subject with { PlatformId = driver.PlatformId, AccountScope = driver.AccountScope },
            Group with { PlatformId = driver.PlatformId, AccountScope = driver.AccountScope });
        Probe.Check(Key(Driver) == ExactKey && Key(Driver) == Key(Driver),
            "scoped key matches frozen v1 synthetic source vector; actual=" + Key(Driver));
        Probe.Check(Key(Driver with { AccountScope = "other-account" }) ==
            "legacy-profile:v1:BEC63748244AB82CFAF7B45EB8A387D49B706567B9563684F0AF8A7A6F504F43",
            "scoped key matches frozen foreign account vector");
        Probe.Check(Key(Driver with { InstanceId = "other-instance" }) ==
            "legacy-profile:v1:AFC67295EC5E0F0272523B1475FF83A9DC53CB68C4A1A27B3468E80B84FA8C23",
            "scoped key matches frozen foreign instance vector");
        var keys = new[]
        {
            ExactKey, Key(Driver with { PlatformId = "other-platform" }),
            Key(Driver with { AccountScope = "other-account" }), Key(Driver with { InstanceId = "other-instance" }),
            LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = "10003" }, Group),
            LegacyProfileScopeKey.TryCreate(Driver, Subject, Group with { NativeTargetId = "10005" }),
            LegacyProfileScopeKey.TryCreate(Driver, Subject, Group with { Kind = ConversationKind.PrivateChat }),
            LegacyProfileScopeKey.TryCreate(Driver, Subject, Group with { Kind = ConversationKind.ChannelChat }),
            LegacyProfileScopeKey.TryCreate(Driver, Subject, Group with { ThreadId = "thread-a" }),
            LegacyProfileScopeKey.TryCreate(Driver, Subject, Group with { ThreadId = "thread-b" }),
            Key(Driver with { PlatformId = "QQ.PRIVATE" }), Key(Driver with { AccountScope = "LEGACY" })
        };
        Probe.Check(keys.All(LegacyProfileScopeKey.IsScopedKey) && keys.Distinct(StringComparer.Ordinal).Count() == keys.Length,
            "platform account instance subject conversation kind thread and case have distinct keys");
        var digest = ExactKey[LegacyProfileScopeKey.Prefix.Length..];
        Probe.Check(ExactKey != Subject.NativeUserId && !digest.Contains(Driver.PlatformId, StringComparison.Ordinal)
            && !digest.Contains(Driver.AccountScope, StringComparison.Ordinal), "scoped key never reveals scalar identifiers or driver labels");
        Probe.Check(LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = ExactKey }, Group) != ExactKey,
            "native scalar UID resembling the namespace is rehashed rather than trusted");
        var first = LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = "member:with" }, Group with { NativeTargetId = "delimiter" });
        var second = LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = "member" }, Group with { NativeTargetId = "with:delimiter" });
        Probe.Check(first is not null && second is not null && first != second, "length encoding prevents separator ambiguity across native identifiers");
    }

    private static void EncodingAndFormat()
    {
        Probe.Check(LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = "user-\U0001F600" },
            Group with { ThreadId = "thread-\u00E9" }) ==
            "legacy-profile:v1:A36CCAB907E86574FEA91C56A7A26396A33A6FF1CF24EDB61EE97066F91716BA",
            "UTF8 byte length rather than UTF16 character length matches the Unicode vector");
        var composed = LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = "user-\u00E9" }, Group);
        var decomposed = LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = "user-e\u0301" }, Group);
        Probe.Check(composed is not null && decomposed is not null && composed != decomposed,
            "source Unicode normalization is never implicit");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Probe.Check(LegacyProfileScopeKey.TryCreate(Driver, Subject, Group) == ExactKey, "key serialization is culture independent");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Probe.Check(LegacyProfileScopeKey.IsScopedKey(ExactKey), "display recognition accepts canonical scoped key");
        foreach (var invalid in new[] { null, "", "10002", ExactKey.ToLowerInvariant(), ExactKey + "A", ExactKey[..^1],
            "legacy-profile:v2:" + new string('A', 64), LegacyProfileScopeKey.Prefix + new string('G', 64) })
            Probe.Check(!LegacyProfileScopeKey.IsScopedKey(invalid), "display recognition rejects noncanonical scalar or malformed key");
        Probe.Check(LegacyProfileScopeKey.TryCreate(Driver, Subject with { NativeUserId = new string('x', 256) }, Group) is not null,
            "maximum valid identity atom is retained exactly");
    }

    private static void InvalidInputs()
    {
        var invalid = new (DriverKey Driver, ParticipantId Subject, ConversationId Conversation)[]
        {
            (default, Subject, Group), (Driver with { PlatformId = null! }, Subject, Group),
            (Driver with { AccountScope = null! }, Subject, Group), (Driver with { InstanceId = null! }, Subject, Group),
            (Driver with { InstanceId = " " }, Subject, Group), (Driver with { InstanceId = "instance\n" }, Subject, Group),
            (Driver with { InstanceId = new string('i', 257) }, Subject, Group), (Driver, null!, Group), (Driver, Subject, null!),
            (Driver, Subject with { PlatformId = "other-platform" }, Group),
            (Driver, Subject with { AccountScope = "other-account" }, Group),
            (Driver, Subject, Group with { PlatformId = "other-platform" }),
            (Driver, Subject, Group with { AccountScope = "other-account" }),
            (Driver, Subject with { NativeUserId = null! }, Group), (Driver, Subject with { NativeUserId = "" }, Group),
            (Driver, Subject with { NativeUserId = " " }, Group), (Driver, Subject with { NativeUserId = " member" }, Group),
            (Driver, Subject with { NativeUserId = "member " }, Group), (Driver, Subject with { NativeUserId = "member\0" }, Group),
            (Driver, Subject with { NativeUserId = new string('x', 257) }, Group),
            (Driver, Subject with { NativeUserId = "\uD800" }, Group), (Driver, Subject with { NativeUserId = "\uDC00" }, Group),
            (Driver, Subject, Group with { NativeTargetId = null! }), (Driver, Subject, Group with { NativeTargetId = "" }),
            (Driver, Subject, Group with { NativeTargetId = "\n" }),
            (Driver, Subject, Group with { Kind = ConversationKind.LocalTest }),
            (Driver, Subject, Group with { Kind = (ConversationKind)999 }),
            (Driver, Subject, Group with { ThreadId = "" }), (Driver, Subject, Group with { ThreadId = " " }),
            (Driver, Subject, Group with { ThreadId = "\uD800" }), (Driver, Subject, Group with { ThreadId = new string('t', 257) })
        };
        foreach (var value in invalid)
            Probe.Check(LegacyProfileScopeKey.TryCreate(value.Driver, value.Subject, value.Conversation) is null,
                "invalid provenance yields no key and cannot fall back to scalar identity");
    }

    private static void GetterBoundary()
    {
        using var f = new Fixture();
        var envelope = f.Envelope();
        using var execution = f.Enter(envelope);
        var current = LegacyProfileScopeKey.TryCreate(Fixture.Driver, Fixture.Actor, Fixture.Group.Conversation)!;
        var foreignDriver = Fixture.Driver with { AccountScope = "other-account" };
        var foreign = LegacyProfileScopeKey.TryCreate(foreignDriver,
            Fixture.Actor with { AccountScope = foreignDriver.AccountScope },
            Fixture.Group.Conversation with { AccountScope = foreignDriver.AccountScope })!;
        var foreignInstance = LegacyProfileScopeKey.TryCreate(Fixture.Driver with { InstanceId = "other-instance" },
            Fixture.Actor, Fixture.Group.Conversation)!;
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Fixture.Actor.NativeUserId] = "synthetic unscoped profile",
            [foreign] = "synthetic foreign account profile", [foreignInstance] = "synthetic foreign instance profile"
        };
        using (var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope))
        {
            string? requestedKey = null;
            var profile = turn.ReadLegacyProfile(Fixture.Actor, key => { requestedKey = key; return blobs.GetValueOrDefault(key, ""); });
            Probe.Check(requestedKey == current && profile == "" && !MemoryUseScope.HasMemory,
                "actual turn requests exact source key and cannot read scalar or foreign provenance blobs");
            blobs[current] = "synthetic exact-source profile";
            Probe.Check(turn.ReadLegacyProfile(Fixture.Actor, key => blobs.GetValueOrDefault(key, "")) == "synthetic exact-source profile"
                && MemoryUseScope.HasMemory && Probe.Model() && Probe.Send(Fixture.Group),
                "exact-source blob reaches an authorized model and matching destination");
        }
        using (var turn = UnifiedMemoryTurn.Begin(null, envelope))
        {
            var called = false;
            Probe.Check(turn.ReadLegacyProfile(Fixture.Actor, _ => { called = true; return "synthetic profile"; }) == ""
                && !called && !MemoryUseScope.HasMemory, "null core grants no legacy authority and never calls the getter");
        }
        using (var turn = UnifiedMemoryTurn.Begin(f.Memory, envelope))
        {
            var called = false;
            Probe.Check(turn.ReadLegacyProfile(Fixture.Actor with { NativeUserId = "\uD800" }, _ => { called = true; return "synthetic profile"; }) == ""
                && !called && !MemoryUseScope.HasMemory, "invalid key blocks getter before storage exposure");
        }
    }
}
