using System.Reflection;
using BotAgent.Services;
using BotAgent.Engine.Runtime;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Reply;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Platforms;
using BotAgent.Services.Participation;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

internal static class PrivateReplyChecks
{
    public static async Task RunAsync()
    {
        Inbound(false, false, true);
        Inbound(true, false, false);
        Inbound(true, true, true);
        await LoopScopeAsync();
    }

    private static void Inbound(bool isGroup, bool mentioned, bool expectedDirect)
    {
        var settings = new AppSettings { PrivateChatEnabled = true, EnableApprovals = false,
            WhitelistGroups = "10001", WhitelistPrivates = "10002" };
        var box = new Box(settings);
        var source = Stub<IQqChatSource>((_, _) => null);
        var registry = new ConversationRegistry(Stub<IConversationRepository>((_, _) => null), box, source, _ => true, _ => { });
        var policy = Policy();
        var policies = Stub<IPlatformPolicyResolver>((method, _) => method.Name == "IsChatEnabled" ? true : policy);
        var whitelist = new WhitelistGate(box, policies);
        // Stop at the UI notification immediately after Append, before scheduling any workers.
        // The test exercises the real inbound classification but needs no media/profile persistence.
        var constructor = typeof(ReplyPipeline).GetConstructors().Single();
        var values = constructor.GetParameters().Select(parameter => parameter.Name switch
        {
            "box" => (object)box, "source" => source, "registry" => registry, "whitelist" => whitelist,
            "platformPolicies" => policies,
            "participation" => new ParticipationUseCase(box, _ => { }),
            "hooks" => new ReplyHooks(_ => { }, () => 10003, () => false),
            "ui" => MakeStub(parameter.ParameterType, (_, _) => throw new AppendedCheckpoint()),
            "agentCmds" => MakeStub(parameter.ParameterType, (method, arguments) =>
            {
                if (method.Name != "TryParseCommand") throw new InvalidOperationException(method.Name);
                arguments![1] = string.Empty;
                return false;
            }),
            _ => parameter.HasDefaultValue ? parameter.DefaultValue : null
        }).ToArray();
        var pipeline = (ReplyPipeline)constructor.Invoke(values);
        var incoming = new QqChatMessage(MessageId: 10004, IsGroup: isGroup, UserId: 10002,
            GroupId: isGroup ? 10001 : 0, SenderName: "Synthetic peer", Text: "你好",
            Time: DateTimeOffset.UnixEpoch, MentionedSelf: mentioned, Channel: Channels.Private);
        var checkpoint = false;
        try { pipeline.HandleInbound(incoming); }
        catch (AppendedCheckpoint) { checkpoint = true; }
        Ensure(checkpoint, "inbound reaches post-append checkpoint");
        var appended = registry.GetOrCreate(incoming)!.TakeLast(1).Single();
        Ensure(appended.DirectToBot == expectedDirect, $"direct addressing: group={isGroup}, mentioned={mentioned}");
    }

    private static async Task LoopScopeAsync()
    {
        var received = new List<bool>();
        var model = Stub<IModelClient>((method, arguments) =>
        {
            if (method.Name != "CompleteAsync") throw new InvalidOperationException(method.Name);
            var index = Array.FindIndex(method.GetParameters(), value => value.Name == "isPrivateConversation");
            received.Add((bool)arguments![index]!);
            return Task.FromResult(new CompletionResult(100, "Synthetic reply", "{}"));
        });
        var caps = ChatCapabilitySet.FromSwitches(true, false, false, false, false, null);
        var platform = Policy();
        var settings = new AppSettings();
        var turn = new TurnInputs(DateTime.UnixEpoch, settings, caps, platform,
            new[] { new ChatMessage { Role = MessageRole.Peer, Text = "你好", Timestamp = DateTimeOffset.UnixEpoch } },
            new(), 0, new(), 0, null, null, "中性", false, null, null, null, null, null);
        var loop = new AgentTurnLoop(model);
        await loop.RunAsync("private:10002", turn with { IsPrivateConversation = true }, caps, false, 2,
            _ => Task.FromResult<string?>(received.Count == 1 ? "Synthetic tool note" : null));
        await loop.RunAsync("group:10001", turn, caps, false, 1, _ => Task.FromResult<string?>(null));
        Ensure(received.SequenceEqual(new[] { true, true, false }), "every private loop step carries scope; groups remain false");
    }

    private static EffectivePlatformPolicy Policy() => new(
        new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy), true, true, true, true,
        PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>());

    private static void Ensure(bool condition, string message)
    { if (!condition) throw new Exception("Private reply regression: " + message); }
    private sealed class Box(AppSettings settings) : ISettingsBox { public AppSettings Current => settings; }
    private sealed class AppendedCheckpoint : Exception { }
    private static T Stub<T>(Func<MethodInfo, object?[]?, object?> call) where T : class => (T)MakeStub(typeof(T), call);
    private static object MakeStub(Type contract, Func<MethodInfo, object?[]?, object?> call)
    {
        var proxy = DispatchProxy.Create(contract, typeof(PortStub));
        ((PortStub)proxy).Call = call;
        return proxy;
    }
    public class PortStub : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => Call(method!, arguments);
    }
}
