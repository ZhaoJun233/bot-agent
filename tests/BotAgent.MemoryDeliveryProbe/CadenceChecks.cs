using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Conversations;
using BotAgent.Services.Local;
using BotAgent.Services.Memory;
using BotAgent.Services.Ops;
using BotAgent.Services.Reply;

internal static partial class Probe
{
    private static async Task CadenceChecks()
    {
        await Run("delivery.real-cadence-stops-after-first-invalidated-segment", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            using var local = new LocalChannelSource(_ => receipt.Invalidate());
            var driver = Driver(PlatformId.Local);
            MemoryUseScope.RestrictDestination(driver, Conversation(driver, true, "10001"));
            using var lease = SourceLease(local, driver);
            var sender = new PlainSender(new Settings(), local, null!, null!,
                new OwnMessageLedger(new Ledger(), _ => { }), _ => { }, new TurnTraceStore());
            var report = await sender.SendWithCadenceAsync(true, 10001, "First synthetic sentence。Second synthetic sentence。Third synthetic sentence。", null);
            Require(report.SentSegments.Count == 1 && local.Outbox.Count == 1);
            Require(report.FailureReasonCode == "segment_failed");
        });
    }

    private sealed class Settings : ISettingsBox
    {
        public AppSettings Current { get; } = new() { SplitReplies = true, SegmentDelayMs = 0, FilterActionNarration = false };
    }

    private sealed class Ledger : IOwnMessageRepository
    {
        public int MaxEntries => 200;
        public List<OwnMessage> LoadRecent(int max) => [];
        public void Upsert(long id, string text, DateTimeOffset at) { }
        public void Upsert(MessageRef messageRef, string text, DateTimeOffset at) { }
        public void PruneTo(int max) { }
    }
}
