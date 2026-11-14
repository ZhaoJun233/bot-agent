using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Engine.Runtime;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Official;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Permissions;
using BotAgent.Services.Qq;

internal static class MemberRoleChecks
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Dispatch(OfficialBotGateway gateway, string kind, JsonObject data)
        => typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, [kind, data, null]);
    private static JsonObject Message(FrozenClock clock, string role = "admin", string group = "group-A", string member = "member-A")
        => new() { ["id"] = Guid.NewGuid().ToString("N"), ["group_openid"] = group, ["timestamp"] = clock.Now.ToString("O"),
            ["author"] = new JsonObject { ["member_openid"] = member, ["member_role"] = role, ["username"] = "Synthetic member" },
            ["content"] = "synthetic" };
    private static OfficialMemberRoleEvidence Evidence(QqChatMessage message)
        => message.OfficialRole ?? throw new InvalidOperationException("missing role evidence");

    public static async Task RunAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        EngineClock.Initialize(clock);
        await test("role: scoped event evidence has provenance and is excluded from JSON", () =>
        {
            using var f = new Fixture(); var message = f.Receive(Message(clock)); var evidence = Evidence(message);
            Require(evidence.Subject.Scope.AppId == "app-A" && evidence.Subject.Scope.Conversation!.NativeTargetId == "group-A"
                && evidence.Subject.NativeId == "member-A" && evidence.Source == "GROUP_MESSAGE_CREATE"
                && evidence.EventTime == clock.Now && evidence.ObservedAt == clock.Now && evidence.IsFresh(clock.Now), "incomplete scope/provenance");
            Require(evidence.Role == OfficialMemberRole.Admin && message.SenderRole is null, "official role became legacy authority");
            Require(!JsonSerializer.SerializeToNode(message)!.AsObject().ContainsKey("OfficialRole"), "role evidence serialized");
            var wire = JsonSerializer.SerializeToNode(message)!.AsObject(); wire["OfficialRole"] = new JsonObject { ["Role"] = "Owner" };
            Require(wire.Deserialize<QqChatMessage>()!.OfficialRole is null, "wire body injected evidence");
            return Task.CompletedTask;
        });
        await test("role: demotion revokes held evidence and old or equal-time elevation cannot restore it", () =>
        {
            using var f = new Fixture(); var old = Message(clock); var admin = Evidence(f.Receive(old));
            clock.Now += TimeSpan.FromSeconds(1); var member = Evidence(f.Receive(Message(clock, "member")));
            old["id"] = "late-admin"; Require(f.Receive(old).OfficialRole is null, "older role overwrote demotion");
            Require(f.Receive(Message(clock, "owner")).OfficialRole is null, "equal timestamp elevated role");
            Require(!admin.IsFresh(clock.Now) && member.IsFresh(clock.Now), "held elevated proof remained live");
            clock.Now += TimeSpan.FromSeconds(1); Require(Evidence(f.Receive(Message(clock, "owner"))).Role == OfficialMemberRole.Owner,
                "genuinely newer role was rejected");
            return Task.CompletedTask;
        });
        await test("role: unknown and equal-time demotion revoke elevation without inferring from username", () =>
        {
            using var f = new Fixture(); var admin = Evidence(f.Receive(Message(clock)));
            var input = Message(clock, "OWNER"); input["author"]!["username"] = "owner admin";
            var unknown = Evidence(f.Receive(input));
            Require(unknown.Role == OfficialMemberRole.Unknown && !admin.IsFresh(clock.Now), "unknown role retained authority");
            return Task.CompletedTask;
        });
        await test("role: application and group boundaries never share the same raw member", () =>
        {
            using var a = new Fixture(); using var b = new Fixture("app-B");
            var first = Evidence(a.Receive(Message(clock))); var otherGroup = Evidence(a.Receive(Message(clock, group: "group-B")));
            var otherApp = Evidence(b.Receive(Message(clock)));
            clock.Now += TimeSpan.FromSeconds(1); a.Receive(Message(clock, "member"));
            Require(!first.IsFresh(clock.Now) && otherGroup.IsFresh(clock.Now) && otherApp.IsFresh(clock.Now)
                && first.Subject != otherGroup.Subject && first.Subject != otherApp.Subject, "scope crossed");
            return Task.CompletedTask;
        });
        await test("role: expiry and malformed or future event time cannot be renewed into elevation", () =>
        {
            using var f = new Fixture(); var old = Message(clock); var evidence = Evidence(f.Receive(old));
            clock.Now += TimeSpan.FromMinutes(5); Require(!evidence.IsFresh(clock.Now), "five minute boundary still fresh");
            old["id"] = "expired-replay"; f.Reject(old, "old timestamp renewed proof");
            foreach (JsonNode? stamp in new JsonNode?[] { null, JsonValue.Create("bad"), JsonValue.Create(clock.Now.AddSeconds(1).ToString("O")), JsonValue.Create(long.MaxValue) })
            {
                var input = Message(clock); input["timestamp"] = stamp;
                f.Reject(input, "invalid timestamp granted proof");
            }
            return Task.CompletedTask;
        });
        await test("role: removal revokes outstanding proof and blocks delayed events without allocating aliases", () =>
        {
            using var f = new Fixture(); var old = Message(clock); var evidence = Evidence(f.Receive(old)); var count = f.Ids.Count;
            clock.Now += TimeSpan.FromSeconds(1);
            Dispatch(f.Gateway, "GROUP_MEMBER_REMOVE", new() { ["group_openid"] = "group-A", ["member_openid"] = "member-A",
                ["timestamp"] = clock.Now.ToUnixTimeSeconds() });
            Require(!evidence.IsFresh(clock.Now) && f.Ids.Count == count, "removal kept proof or allocated identity");
            old["id"] = "after-removal"; Require(f.Receive(old).OfficialRole is null, "late event restored removed role");
            return Task.CompletedTask;
        });
        await test("role: whole-second removal wins over fractional messages in the same second", () =>
        {
            using var f = new Fixture(); clock.Now += TimeSpan.FromMilliseconds(250);
            var held = Evidence(f.Receive(Message(clock)));
            Dispatch(f.Gateway, "GROUP_MEMBER_REMOVE", new() { ["group_openid"] = "group-A", ["member_openid"] = "member-A",
                ["timestamp"] = clock.Now.ToUnixTimeSeconds() });
            Require(!held.IsFresh(clock.Now), "whole-second removal was treated as older than the message");
            clock.Now += TimeSpan.FromMilliseconds(100);
            Require(f.Receive(Message(clock)).OfficialRole is null, "same-second message undid removal");
            clock.Now += TimeSpan.FromSeconds(1);
            Require(Evidence(f.Receive(Message(clock))).IsFresh(clock.Now), "subsequent second did not admit fresh evidence");
            return Task.CompletedTask;
        });
        await test("role: invalid timestamps cannot lower a removal watermark", () =>
        {
            using var f = new Fixture(); clock.Now = DateTimeOffset.FromUnixTimeSeconds(clock.Now.ToUnixTimeSeconds()).AddMilliseconds(200);
            f.Receive(Message(clock));
            Dispatch(f.Gateway, "GROUP_MEMBER_REMOVE", new() { ["group_openid"] = "group-A", ["member_openid"] = "member-A",
                ["timestamp"] = clock.Now.ToUnixTimeSeconds() });
            var malformed = Message(clock); malformed["timestamp"] = "invalid"; f.Reject(malformed, "invalid timestamp reached host");
            clock.Now += TimeSpan.FromMilliseconds(100);
            Require(f.Receive(Message(clock)).OfficialRole is null, "invalid time lowered the removal watermark");
            return Task.CompletedTask;
        });
        await test("role: app retirement revokes held evidence and cannot be undone by reverting settings", () =>
        {
            using var f = new Fixture(); var proof = Evidence(f.Receive(Message(clock)));
            f.Options.OfficialAppId = "app-B"; Dispatch(f.Gateway, "GROUP_MESSAGE_CREATE", Message(clock));
            Require(!proof.IsFresh(clock.Now) && f.Gateway.LastErrorCode == "identity_source_changed", "app retirement retained role");
            f.Options.OfficialAppId = "app-A"; Dispatch(f.Gateway, "GROUP_MESSAGE_CREATE", Message(clock));
            Require(f.Gateway.LastErrorCode == "identity_source_changed", "retired instance rearmed");
            return Task.CompletedTask;
        });
        await test("role: group lifecycle and disconnected gateways revoke proof even if already disconnected", () =>
        {
            foreach (var kind in new[] { "GROUP_DEL_ROBOT", "GROUP_ADD_ROBOT", "READY", "disconnect", "dispose" })
            {
                using var f = new Fixture(); var input = Message(clock); var evidence = Evidence(f.Receive(input));
                clock.Now += TimeSpan.FromSeconds(1);
                if (kind == "disconnect") typeof(OfficialBotGateway).GetMethod("SetConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Gateway, [false]);
                else if (kind == "dispose") f.Gateway.Dispose();
                else Dispatch(f.Gateway, kind, new() { ["group_openid"] = "group-A", ["session_id"] = "synthetic" });
                Require(!evidence.IsFresh(clock.Now), "lifecycle retained proof: " + kind);
                if (kind != "dispose")
                {
                    input["id"] = "late";
                    if (kind == "GROUP_DEL_ROBOT") f.Reject(input, "removed relationship replay reached host");
                    else Require(f.Receive(input).OfficialRole is null, "lifecycle replay elevated");
                }
            }
            return Task.CompletedTask;
        });
        await test("role: cache saturation preserves revocation watermarks", () =>
        {
            using var f = new Fixture(); var observe = typeof(OfficialBotGateway).GetMethod("ObserveMemberRole", BindingFlags.Instance | BindingFlags.NonPublic)!;
            OfficialMemberRoleEvidence? Add(string member, string role = "admin")
                => (OfficialMemberRoleEvidence?)observe.Invoke(f.Gateway, [Message(clock, role),
                    new OfficialIdentityKey(new("app-A", OfficialIdentityKind.GroupMember,
                        new(PlatformId.QqOfficial, "app-A", ConversationKind.GroupChat, "group-A")), member), true]);
            var proof = Add("first")!; clock.Now += TimeSpan.FromSeconds(1); Add("first", "member");
            for (var index = 1; index < 2048; index++) Require(Add("member-" + index) is not null, "cache filled prematurely");
            Require(Add("overflow") is null && Add("first", "owner") is null && !proof.IsFresh(clock.Now), "capacity discarded a demotion");
            clock.Now = DateTimeOffset.FromUnixTimeSeconds(clock.Now.ToUnixTimeSeconds()).AddMilliseconds(200);
            Dispatch(f.Gateway, "GROUP_MEMBER_REMOVE", new() { ["group_openid"] = "group-A", ["member_openid"] = "overflow",
                ["timestamp"] = clock.Now.ToUnixTimeSeconds() });
            clock.Now += TimeSpan.FromMilliseconds(100);
            Require(Add("overflow") is null, "full cache lost whole-second removal barrier");
            clock.Now += TimeSpan.FromMinutes(5); Require(Add("overflow") is not null, "expired cache failed to release capacity");
            return Task.CompletedTask;
        });
        await test("role: display names use trusted text or alias without native suffix", () =>
        {
            using var f = new Fixture(); var good = f.Receive(Message(clock)); Require(good.SenderName == "Synthetic member", "username ignored");
            foreach (JsonNode? name in new JsonNode?[] { null, JsonValue.Create(42), JsonValue.Create(" "), JsonValue.Create("bad\nname"), JsonValue.Create(new string('x', 129)) })
            {
                var input = Message(clock, member: "native-secret-TAIL"); input["author"]!["username"] = name;
                var message = f.Receive(input); Require(message.SenderName == "官方用户" + message.UserId && !message.SenderName.Contains("TAIL"), "native suffix leaked");
            }
            return Task.CompletedTask;
        });
        await test("role: official context never touches private repository and rechecks revocation", () =>
        {
            using var f = new Fixture(); var store = DispatchProxy.Create<IMemberRoleRepository, RejectCalls>();
            var roles = new MemberRoleUseCase(f.Gateway, store, _ => { }); var message = f.Receive(Message(clock));
            roles.Remember(message); var key = Channels.Key(message.Channel, true, message.GroupId);
            Require(roles.DescribeForPrompt(key, message.GroupId, 14, out var count)?.Contains("管理员") == true && count == 1, "fresh scoped context missing");
            Require(roles.DescribeForPrompt(message.GroupId, 14, out _) is null, "legacy official prompt consulted private store");
            Require(roles.DescribeForPrompt("official:group:8000000000000001", message.GroupId, 14, out _) is null, "other conversation inherited role");
            clock.Now += TimeSpan.FromSeconds(1); f.Receive(Message(clock, "member"));
            Require(roles.DescribeForPrompt(key, message.GroupId, 14, out count) is null && count == 0, "consumer kept revoked proof without new Remember");
            return Task.CompletedTask;
        });
        await test("role: context uses the resolved structured conversation key", () =>
        {
            using var f = new Fixture(); var message = f.Receive(Message(clock));
            var roles = new MemberRoleUseCase(f.Gateway, DispatchProxy.Create<IMemberRoleRepository, RejectCalls>(), _ => { });
            var key = ConversationIdCodec.Encode(new(PlatformId.QqOfficial, "account-A", ConversationKind.GroupChat, message.GroupId.ToString()));
            roles.Remember(message, key);
            Require(roles.DescribeForPrompt(key, message.GroupId, 14, out var count) is not null && count == 1, "structured source lost role");
            Require(roles.DescribeForPrompt(Channels.Key(message.Channel, true, message.GroupId), message.GroupId, 14, out _) is null,
                "structured role leaked through legacy key");
            return Task.CompletedTask;
        });
        await test("role: source lifetime and driver instance are rechecked at context consumption", () =>
        {
            using var f = new Fixture(); using var lifetime = new CancellationTokenSource();
            var roles = new MemberRoleUseCase(f.Gateway, DispatchProxy.Create<IMemberRoleRepository, RejectCalls>(), _ => { });
            var message = f.Receive(Message(clock)) with { SourceLifetime = lifetime.Token };
            var key = Channels.Key(message.Channel, true, message.GroupId);
            var source = new PlatformSourceLease(new Owner(), f.Gateway,
                new PlatformMessageOrigin(new(PlatformId.QqOfficial, AccountScope.Legacy, "first")), lifetime.Token, () => true);
            var other = new PlatformSourceLease(new Owner(), f.Gateway,
                new PlatformMessageOrigin(new(PlatformId.QqOfficial, AccountScope.Legacy, "other")), lifetime.Token, () => true);
            using (PlatformSourceExecution.Enter(source))
            {
                roles.Remember(message);
                Require(roles.DescribeForPrompt(key, message.GroupId, 14, out _) is not null, "source-bound role absent");
            }
            using (PlatformSourceExecution.Enter(other))
                Require(roles.DescribeForPrompt(key, message.GroupId, 14, out _) is null, "another instance consumed source role");
            lifetime.Cancel();
            using (PlatformSourceExecution.Enter(source))
                Require(roles.DescribeForPrompt(key, message.GroupId, 14, out _) is null, "retired source kept role context");
            return Task.CompletedTask;
        });
        await test("role: official owner cannot approve an Agent request but explicit approver can", async () =>
        {
            foreach (var configured in new[] { false, true })
            {
                var logs = new List<string>(); var replies = new List<string>();
                var box = new Box(new AppSettings { EnableApprovals = true, ApprovalApprovers = configured ? "8000000000000002" : "" });
                var approvals = new ApprovalUseCase(box, new ApprovalHooks(logs.Add, (text, _) => text,
                    (_, text) => { replies.Add(text); return Task.CompletedTask; }, (_, text) => { replies.Add(text); return Task.CompletedTask; }),
                    new SessionPolicyLedger(), new TurnTraceStore());
                var conversation = new BotConversation { SourceKey = "official:group:8000000000000001", Kind = ConversationKind.GroupChat, Name = "Synthetic" };
                Require(await approvals.OpenApprovalForTool(conversation, ApprovalFlow.FixedToolId, 1, approvals.Capabilities), "request not created");
                var pending = approvals.PendingApprovals().Single();
                Require(pending.ApproverRoles is { Count: 0 } && replies.All(text => !text.Contains("群主/管理员")), "request advertised QQ role authority");
                var message = new QqChatMessage(2, true, 8000000000000002, 8000000000000001, "Synthetic", "同意 " + pending.RequestId,
                    clock.Now, false, SenderRole: "owner", Channel: Channels.Official);
                Require(approvals.HandleCommand(message), "approval command not handled");
                Require(configured ? approvals.PendingApprovals().Count == 0 : approvals.PendingApprovals().Count == 1
                    && logs.Any(line => line.Contains("not_an_approver")), "official role or explicit list authority incorrect");
            }
        });
    }

    private sealed class Box(AppSettings settings) : ISettingsBox { public AppSettings Current => settings; }
    private sealed class Owner : IPlatformSourceOwner
    {
        public string? ConversationKey(QqChatMessage message, Func<string, bool> exists)
            => Channels.Key(message.Channel, message.IsGroup, message.IsGroup ? message.GroupId : message.UserId);
    }
    public class RejectCalls : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => throw new InvalidOperationException("unexpected external call: " + method?.Name);
    }
    private sealed class Fixture : IDisposable
    {
        private readonly SyntheticDirectory _directory = new();
        public OfficialIdentityMap Ids { get; }
        public OfficialBotGateway Gateway { get; }
        public PlatformOptions Options { get; }
        private readonly List<QqChatMessage> _messages = new();
        public Fixture(string app = "app-A")
        {
            Ids = new OfficialIdentityMap(_directory.Map);
            Options = new PlatformOptions { OfficialAppId = app };
            Gateway = new(new PlatformSettingsBox(Options),
                DispatchProxy.Create<IPlatformHttpFetcher, RejectCalls>(), () => Ids, _ => { }, () => OfficialProbeContext.OpenLedger(_directory.Map, app));
            Gateway.MessageReceived += _messages.Add;
        }
        public QqChatMessage Receive(JsonObject data)
        {
            var count = _messages.Count; Dispatch(Gateway, "GROUP_MESSAGE_CREATE", data);
            Require(_messages.Count == count + 1, "valid message was not delivered"); return _messages[^1];
        }
        public void Reject(JsonObject data, string reason)
        {
            var count = _messages.Count; Dispatch(Gateway, "GROUP_MESSAGE_CREATE", data);
            Require(_messages.Count == count, reason);
        }
        public void Dispose() { Gateway.Dispose(); _directory.Dispose(); }
    }
}
