using BotAgent.Domain.Memory;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Platforms;
using BotAgent.Engine.Memory;
using BotAgent.Services.Local;
using BotAgent.Services.Memory;

internal static partial class Probe
{
    private static async Task LifecycleChecks()
    {
        foreach (var transition in new[] { "revoke", "forget", "edit", "disable" })
        {
            await Run("core-to-delivery." + transition + "-blocks-model-and-platform", async () =>
            {
                using var local = new LocalChannelSource();
                var driver = Driver(PlatformId.Local);
                var destination = Conversation(driver, true, "10001");
                var actor = new BotAgent.Domain.Messaging.ParticipantId(driver.PlatformId, driver.AccountScope, "10001");
                var source = new UnifiedMemoryContext(actor, driver, Conversation(driver, true, "20001"), true);
                var privateContext = source with { Conversation = Conversation(driver, false, "10001") };
                var targetContext = source with { Conversation = destination };
                var memory = new UnifiedMemory(new MemoryStore());
                Require(memory.SetEnabled(true).Succeeded);
                var request = new UnifiedMemoryRequest(new(UnifiedMemoryFactCategory.ReplyLength, "brief"),
                    [new(driver, destination)], true);
                var candidate = memory.RequestRemember(source, request);
                Require(candidate.Succeeded && candidate.Entry is not null);
                var entry = candidate.Entry!;
                Require(memory.Confirm(privateContext, new(entry.FactId, entry.Version, entry.ConfirmationToken, true, true)).Succeeded);
                var read = memory.Read(targetContext, "synthetic-query");
                Require(read.Entries.Length == 1 && read.Receipt is not null);
                using var scope = MemoryUseScope.Begin();
                MemoryUseScope.Register(commit => memory.TryUseReceipt(read.Receipt!, targetContext, commit));
                MemoryUseScope.RestrictDestination(driver, destination);
                using var lease = SourceLease(local, driver);
                var http = new FakeHttp();
                var model = Transport(http);
                await model.SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None);
                Require((await local.SendTextAsync(true, 10001, "synthetic-derived-before")).Ok);

                var result = transition switch
                {
                    "revoke" => memory.Revoke(privateContext, entry.FactId, new(driver, destination)),
                    "forget" => memory.Forget(privateContext, entry.FactId),
                    "edit" => memory.Edit(privateContext, entry.FactId, entry.Version,
                        request with { Fact = new(UnifiedMemoryFactCategory.ReplyLength, "detailed") }),
                    _ => memory.SetEnabled(false)
                };
                Require(result.Succeeded);
                await IgnoreDenial(() => model.SendAsync(Payload(), 0, Array.Empty<long>(), CancellationToken.None));
                Require(!(await local.SendTextAsync(true, 10001, "synthetic-derived-after")).Ok);
                Require(http.Sends == 1 && local.Outbox.Count == 1);
            });
        }
    }

    private sealed class MemoryStore : IUnifiedMemoryPersistence
    {
        private UnifiedMemoryState _state = UnifiedMemoryState.Empty;
        public UnifiedMemoryState Load() => _state;
        public void Save(long expectedRevision, UnifiedMemoryState state)
        {
            if (_state.Revision != expectedRevision) throw new InvalidOperationException("synthetic_revision_conflict");
            _state = state;
        }
    }
}
