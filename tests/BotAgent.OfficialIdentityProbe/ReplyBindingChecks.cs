using System.Reflection;
using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;

internal static class ReplyBindingChecks
{
    private static readonly ConversationId Target = new(PlatformId.QqOfficial, AccountScope.Legacy,
        ConversationKind.GroupChat, "8000000000000001");
    private static readonly MessageRef First = new(Target, "8000000000000002");
    private static readonly MessageRef Second = new(Target, "8000000000000003");
    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static PlatformSourceLease Source(CancellationToken lifetime = default, Func<bool>? current = null, MessageRef? trigger = null)
        => new(new Owner(), DispatchProxy.Create<IQqChatSource, NoIo>(),
            new(new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-driver"), trigger ?? First),
            lifetime, current ?? (() => true));
    private static IDisposable GenerateBinding(string key, long? id, bool proactive = false)
        => (IDisposable)typeof(ReplyPipeline).GetMethod("BindReplyDelivery", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new BotConversation { SourceKey = key, Kind = ConversationKind.GroupChat, Name = "Synthetic" }, id, proactive])!;

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("reply binding selects generation trigger without replacing source or original origin", () =>
        {
            var source = Source();
            using var ingress = PlatformSourceExecution.Enter(source);
            using var binding = GenerateBinding(ConversationIdCodec.Encode(Target), 8000000000000003);
            Require(ReplyDeliveryExecution.Current is { IsCurrent: true } current && current.Trigger == Second, "generation retained first owner trigger");
            Require(ReferenceEquals(PlatformSourceExecution.Current, source) && ReferenceEquals(ReplyDeliveryExecution.Current!.Source, source)
                && source.Origin.Trigger == First, "memory source identity or origin changed");
            return Task.CompletedTask;
        });
        await test("reply binding explicit empty never borrows a previous human trigger", () =>
        {
            var source = Source(); using var ingress = PlatformSourceExecution.Enter(source);
            using var outer = ReplyDeliveryExecution.Enter(source, First);
            foreach (var (id, proactive, key) in new (long?, bool, string)[]
            {
                (null, false, ConversationIdCodec.Encode(Target)), (0, false, ConversationIdCodec.Encode(Target)),
                (-1, false, ConversationIdCodec.Encode(Target)), (8000000000000003, true, ConversationIdCodec.Encode(Target)),
                (8000000000000003, false, "official:group:8000000000000999"), (8000000000000003, false, "invalid"),
            })
            {
                using var inner = GenerateBinding(key, id, proactive);
                Require(ReplyDeliveryExecution.Current is { Trigger: null }, "empty generation borrowed prior credential");
            }
            Require(ReplyDeliveryExecution.Current?.Trigger == First, "scope did not restore parent");
            return Task.CompletedTask;
        });
        await test("reply binding rejects cross conversation account platform thread and absent provenance", () =>
        {
            var source = Source(); using var ingress = PlatformSourceExecution.Enter(source);
            using var outer = ReplyDeliveryExecution.Enter(source, First);
            foreach (var target in new[]
            {
                Target with { NativeTargetId = "8000000000000999" }, Target with { AccountScope = "other" },
                Target with { PlatformId = PlatformId.QqPrivate }, Target with { ThreadId = "other" },
                Target with { Kind = ConversationKind.PrivateChat },
            })
            {
                var refused = false;
                try { using var ignored = ReplyDeliveryExecution.Enter(source, new(target, Second.NativeMessageId)); }
                catch (ArgumentException) { refused = true; }
                Require(refused && ReplyDeliveryExecution.Current?.Trigger == First, "cross-scope binding escaped or mutated parent");
            }
            foreach (var value in new[] { "", " " })
            {
                var refused = false;
                try { using var ignored = ReplyDeliveryExecution.Enter(source, new(Target, value)); }
                catch (ArgumentException) { refused = true; }
                Require(refused, "invalid message reference escaped");
            }
            var absent = new PlatformSourceLease(source.Owner, source.Source, new(source.Origin.Driver), default, () => true);
            var denied = false;
            try { using var ignored = ReplyDeliveryExecution.Enter(absent, First); }
            catch (ArgumentException) { denied = true; }
            Require(denied, "source without inbound provenance minted a human trigger");
            return Task.CompletedTask;
        });
        await test("reply binding follows source cancellation retirement and exact ambient owner", () =>
        {
            using var lifetime = new CancellationTokenSource(); var live = true;
            var source = Source(lifetime.Token, () => live); using var ingress = PlatformSourceExecution.Enter(source);
            using var binding = ReplyDeliveryExecution.Enter(source, First);
            Require(ReplyDeliveryExecution.Current!.IsCurrent, "current binding not usable");
            using (PlatformSourceExecution.Enter(Source())) Require(!ReplyDeliveryExecution.Current.IsCurrent, "different source consumed binding");
            live = false; Require(!ReplyDeliveryExecution.Current.IsCurrent, "retired driver retained binding");
            live = true; lifetime.Cancel(); Require(!ReplyDeliveryExecution.Current.IsCurrent, "cancelled source retained binding");
            return Task.CompletedTask;
        });
        await test("reply binding async forks stay isolated and restore their parent", async () =>
        {
            var source = Source(); using var ingress = PlatformSourceExecution.Enter(source);
            using var outer = ReplyDeliveryExecution.Enter(source, First);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var child = Task.Run(async () =>
            {
                using (ReplyDeliveryExecution.Enter(source, Second))
                {
                    ready.SetResult(); await release.Task;
                    Require(ReplyDeliveryExecution.Current?.Trigger == Second && ReplyDeliveryExecution.Current.IsCurrent, "child binding was overwritten");
                }
                Require(ReplyDeliveryExecution.Current?.Trigger == First, "child did not restore inherited binding");
            });
            await ready.Task;
            Require(ReplyDeliveryExecution.Current?.Trigger == First, "child mutated parent binding");
            release.SetResult(); await child;
            Require(ReferenceEquals(PlatformSourceExecution.Current, source), "async binding changed source object");
        });
        await test("reply binding contains no serializable identity and duplicate disposal is harmless", () =>
        {
            var source = Source(); using var ingress = PlatformSourceExecution.Enter(source);
            using var outer = ReplyDeliveryExecution.Enter(source, First);
            var inner = ReplyDeliveryExecution.Enter(source, Second);
            Require(JsonSerializer.Serialize(ReplyDeliveryExecution.Current) == "{}"
                && ReplyDeliveryExecution.Current!.ToString() == "ReplyDeliveryBinding", "binding exposed source identity");
            inner.Dispose(); inner.Dispose();
            Require(ReplyDeliveryExecution.Current?.Trigger == First, "duplicate disposal lost outer binding");
            return Task.CompletedTask;
        });
    }

    private sealed class Owner : IPlatformSourceOwner
    {
        public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => throw new InvalidOperationException("unexpected routing IO");
    }
    public class NoIo : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("unexpected source IO");
    }
}
