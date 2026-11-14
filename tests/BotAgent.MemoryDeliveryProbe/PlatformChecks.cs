using System.Net;
using BotAgent.Adapters.Platforms.Feishu;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Platforms;
using BotAgent.Services.Local;
using BotAgent.Services.Memory;
using BotAgent.Services.OneBot;
using BotAgent.Services.Official;
using BotAgent.Services.Qq;

internal static partial class Probe
{
    private static DriverKey Driver(string platform) => new(platform, platform == PlatformId.QqOfficial ? AccountScope.Legacy : AccountScope.Default, "synthetic-driver");
    private static ConversationId Conversation(DriverKey driver, bool group, string id)
        => new(driver.PlatformId, driver.AccountScope, group ? ConversationKind.GroupChat : ConversationKind.PrivateChat, id);

    private static IDisposable SourceLease(IQqChatSource source, DriverKey driver, long? target = null, long? message = null)
        => PlatformSourceExecution.Enter(new PlatformSourceLease(new Owner(), source,
            new PlatformMessageOrigin(driver, target is { } id && message is { } trigger
                ? new MessageRef(Conversation(driver, true, id.ToString()), trigger.ToString()) : null), CancellationToken.None, () => true));

    private sealed class Owner : IPlatformSourceOwner
    {
        public string? ConversationKey(QqChatMessage message, Func<string, bool> exists) => null;
    }

    private static async Task PlatformChecks()
    {
        await Run("delivery.local-no-memory-unchanged", async () =>
        {
            using var local = new LocalChannelSource();
            Require((await local.SendTextAsync(true, 10001, "synthetic-text")).Ok);
            Require(local.Outbox.Count == 1);
        });
        await Run("delivery.local-bound-target-and-driver-only", async () =>
        {
            using var local = new LocalChannelSource();
            using var scope = MemoryUseScope.Begin();
            MemoryUseScope.Register(new MemoryGate().TryUse);
            var driver = Driver(PlatformId.Local);
            MemoryUseScope.RestrictDestination(driver, Conversation(driver, true, "10001"));
            using (SourceLease(local, driver))
            {
                Require((await local.SendTextAsync(true, 10001, "synthetic-allowed")).Ok);
                Require(!(await local.SendTextAsync(true, 10002, "synthetic-other-target")).Ok);
                Require(!(await local.SendTextAsync(false, 10001, "synthetic-other-kind")).Ok);
            }
            using (SourceLease(local, driver with { InstanceId = "other-driver" }))
                Require(!(await local.SendTextAsync(true, 10001, "synthetic-other-driver")).Ok);
            Require(!(await local.SendTextAsync(true, 10001, "synthetic-no-proof")).Ok);
            Require(local.Outbox.Count == 1);
        });
        foreach (var reason in new[] { "revoke", "forget", "edit", "disable" })
        {
            await Run("delivery.local-" + reason + "-blocks-next-segment", async () =>
            {
                using var local = new LocalChannelSource();
                using var scope = MemoryUseScope.Begin();
                var receipt = new MemoryGate();
                MemoryUseScope.Register(receipt.TryUse);
                var driver = Driver(PlatformId.Local);
                MemoryUseScope.RestrictDestination(driver, Conversation(driver, true, "10001"));
                using var lease = SourceLease(local, driver);
                Require((await local.SendTextAsync(true, 10001, "synthetic-first")).Ok);
                receipt.Invalidate();
                Require(!(await local.SendTextAsync(true, 10001, "synthetic-second")).Ok);
                Require(local.Outbox.Count == 1);
            });
        }
        await Run("delivery.official-upload-revoked-before-final-image-message", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var ids = new SyntheticOfficialIds();
            var target = ids.AliasFor("synthetic-openid");
            var http = new FakeHttp
            {
                OnSend = _ =>
                {
                    receipt.Invalidate();
                    return Task.FromResult(FakeHttp.Response("{\"file_info\":\"synthetic-file\",\"ttl\":100} "));
                }
            };
            using var gateway = Official(http, ids);
            var driver = Driver(PlatformId.QqOfficial);
            MemoryUseScope.RestrictDestination(driver, Conversation(driver, true, target.ToString()));
            using var lease = SourceLease(gateway, driver, target, ids.AliasFor(OfficialTrigger()));
            await IgnoreDenial(() => gateway.SendImageAsync(true, target, new byte[] { 1, 2, 3 }));
            Require(http.Sends == 1);
        });
        await Run("delivery.official-token-retry-rechecks-receipt", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var ids = new SyntheticOfficialIds();
            var target = ids.AliasFor("synthetic-openid");
            var http = new FakeHttp
            {
                OnSend = _ =>
                {
                    receipt.Invalidate();
                    return Task.FromResult(FakeHttp.Response("{\"code\":11244}", HttpStatusCode.Unauthorized));
                }
            };
            using var gateway = Official(http, ids);
            var driver = Driver(PlatformId.QqOfficial);
            MemoryUseScope.RestrictDestination(driver, Conversation(driver, true, target.ToString()));
            using var lease = SourceLease(gateway, driver, target, ids.AliasFor(OfficialTrigger()));
            await IgnoreDenial(() => gateway.SendTextAsync(true, target, "synthetic-message"));
            Require(http.Sends == 1 && http.Posts == 2);
        });
        await Run("delivery.official-waiting-send-rechecks-after-send-gate", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var ids = new SyntheticOfficialIds();
            var target = ids.AliasFor("synthetic-openid");
            var started = Signal();
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var http = new FakeHttp { OnSend = _ => { started.TrySetResult(); return response.Task; } };
            using var gateway = Official(http, ids);
            var driver = Driver(PlatformId.QqOfficial);
            MemoryUseScope.RestrictDestination(driver, Conversation(driver, true, target.ToString()));
            using var lease = SourceLease(gateway, driver, target, ids.AliasFor(OfficialTrigger()));
            var first = gateway.SendTextAsync(true, target, "synthetic-first");
            await started.Task;
            var queued = gateway.SendTextAsync(true, target, "synthetic-queued");
            receipt.Invalidate();
            response.SetResult(FakeHttp.Response("{\"id\":\"synthetic-id\"}"));
            await first;
            await IgnoreDenial(() => queued);
            Require(http.Sends == 1);
        });
        await Run("delivery.feishu-rechecks-after-token-await", async () =>
        {
            using var scope = MemoryUseScope.Begin();
            var receipt = new MemoryGate();
            MemoryUseScope.Register(receipt.TryUse);
            var http = new FakeHttp
            {
                OnPost = _ =>
                {
                    receipt.Invalidate();
                    return Task.FromResult(FakeHttp.Response("{\"code\":0,\"tenant_access_token\":\"synthetic-token\",\"expire\":3600}"));
                }
            };
            using var gateway = new FeishuBotGateway(new PlatformSettingsBox(new PlatformOptions
            {
                FeishuEnabled = true, FeishuAppId = "synthetic-app", FeishuAppSecret = "synthetic-secret",
                FeishuVerificationToken = "synthetic-verification", FeishuWhitelist = "*", FeishuApiBase = "https://example.com"
            }), http);
            var context = gateway.Context;
            var driver = new DriverKey(context.PlatformId, context.AccountScope, context.InstanceId);
            var target = Conversation(driver, true, "synthetic-chat");
            MemoryUseScope.RestrictDestination(driver, target);
            await gateway.SendAsync(context, new OutboundMessage(target, "synthetic-text"));
            Require(http.Posts == 1 && http.Sends == 0);
        });
    }

    private static OfficialIdentityKey OfficialTrigger() => new(new("synthetic-app", OfficialIdentityKind.Message,
        new(PlatformId.QqOfficial, "synthetic-app", ConversationKind.GroupChat, "synthetic-openid"), OfficialMessageDirection.Inbound), "synthetic-inbound");
    private static OfficialBotGateway Official(FakeHttp http, BotAgent.Domain.Ports.IOfficialIdentityMap ids)
    {
        ids.AliasFor(OfficialTrigger());
        return new(new PlatformSettingsBox(new PlatformOptions
        { OfficialEnabled = true, OfficialAppId = "synthetic-app", OfficialAppSecret = "synthetic-secret" }), http, () => ids, _ => { }, () =>
        {
            var ledger = new OfficialDeliveryLedger(new MemoryOfficialSnapshot(), "synthetic-app");
            Require(ledger.AcceptTrigger(OfficialTrigger(), PlatformClock.Current.UtcNow).Allowed);
            return ledger;
        });
    }
}

file sealed class MemoryOfficialSnapshot : IOfficialDeliverySnapshot
{
    private string? _value;
    public string? Load() => _value;
    public void Commit(string snapshot) => _value = snapshot;
    public void Dispose() { }
}

file sealed class SyntheticOfficialIds : BotAgent.Domain.Ports.IOfficialIdentityMap
{
    private readonly Dictionary<OfficialIdentityKey, long> _aliases = new();
    public int Count => _aliases.Count;
    public long AliasFor(string target) => AliasFor(new OfficialIdentityKey(new("synthetic-app", OfficialIdentityKind.Group), target));
    public long AliasFor(OfficialIdentityKey key)
    {
        if (!_aliases.TryGetValue(key, out var alias)) _aliases.Add(key, alias = Channels.AliasBase + Count + 1);
        return alias;
    }
    public string? OriginalOf(long alias, OfficialIdentityScope scope)
        => _aliases.FirstOrDefault(pair => pair.Value == alias && pair.Key.Scope == scope).Key?.NativeId;
}
