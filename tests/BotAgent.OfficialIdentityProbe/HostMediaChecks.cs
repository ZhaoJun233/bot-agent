using System.Reflection;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;
using BotAgent.Services.OneBot;

internal static class HostMediaChecks
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static PlatformSourceLease Lease(ChannelRouter router, IQqChatSource source, Func<bool>? current = null)
        => new(router, source, new(new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-host-media"), null),
            CancellationToken.None, current ?? (() => true));
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("host music text fallback declares no native card and retains all policy gates", () =>
        {
            var policy = new EffectivePlatformPolicy(new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-music"),
                true, true, true, true, PlatformCapabilities.QqOfficial, new HashSet<string>(), new Dictionary<string, bool>(), Array.Empty<string>());
            Require(!policy.Capabilities.SupportsMusic && policy.Capabilities.SupportsMusicTextFallback, "native card capability misdeclared");
            Require(policy.Feature("music") is { Enabled: true, ReasonCode: "music_text_fallback" }, "text fallback unavailable or unexplained");
            foreach (var denied in new[] { policy with { Registered = false }, policy with { Connected = false }, policy with { Enabled = false },
                policy with { ChatEnabled = false }, policy with { FeatureOverrides = new Dictionary<string, bool> { ["music"] = false } },
                policy with { Capabilities = policy.Capabilities with { SupportsText = false } } })
                Require(!denied.Feature("music").Enabled, "fallback bypassed policy");
            Require(!policy.Feature("music", false).Enabled && (policy with { Capabilities = PlatformCapabilities.QqOneBot }).Feature("music").ReasonCode == "enabled", "global gate or native behavior changed");
            return Task.CompletedTask;
        });
        await test("host router preserves media receipts without calling boolean wrappers", async () =>
        {
            var source = DispatchProxy.Create<IQqChatSource, MediaSource>(); var fake = (MediaSource)(object)source;
            using var router = new ChannelRouter(new[] { source });
            foreach (var delivery in new[] { DeliveryResult.Unknown("synthetic_unknown"), DeliveryResult.Throttled(7),
                DeliveryResult.Rejected("synthetic_denied"), DeliveryResult.Degraded("10002", "synthetic_media"), DeliveryResult.Ok("10002") })
            {
                fake.Result = new(delivery.IsSuccess) { Delivery = delivery };
                Require(await router.SendImageWithReceiptAsync(true, Channels.AliasBase + 1, new byte[] { 1 }) == fake.Result, "image receipt changed");
                Require(await router.SendVoiceWithReceiptAsync(true, Channels.AliasBase + 1, "https://example.com/synthetic") == fake.Result, "voice receipt changed");
                Require(await ((IQqChatSource)router).SendMusicWithReceiptAsync(true, Channels.AliasBase + 1, "163", "10001") == fake.Result, "music receipt changed");
            }
            Require(fake.Calls == 15, "unexpected media dispatch count");
        });
        await test("host media receipt path retains source lifetime and owner gates", async () =>
        {
            var source = DispatchProxy.Create<IQqChatSource, MediaSource>(); var fake = (MediaSource)(object)source;
            using var router = new ChannelRouter(new[] { source }); using var foreign = new ChannelRouter(new[] { source });
            foreach (var lease in new[] { Lease(router, source, () => false), Lease(foreign, source) })
            using (PlatformSourceExecution.Enter(lease))
            {
                Require(!(await router.SendImageWithReceiptAsync(true, Channels.AliasBase + 1, new byte[] { 1 })).Ok, "retired image source admitted");
                Require(!(await router.SendVoiceWithReceiptAsync(true, Channels.AliasBase + 1, "https://example.com/synthetic")).Ok, "retired voice source admitted");
                Require(!(await ((IQqChatSource)router).SendMusicWithReceiptAsync(true, Channels.AliasBase + 1, "163", "10001")).Ok, "retired music source admitted");
            }
            Require(fake.Calls == 0, "blocked source reached media transport");
        });
        await test("host media receipt path retains outbound feature authorization", async () =>
        {
            var source = DispatchProxy.Create<IQqChatSource, MediaSource>(); var fake = (MediaSource)(object)source;
            using var router = new ChannelRouter(new[] { source });
            var features = new List<string>();
            Func<PlatformContext, bool, long, string, bool> deny = (_, _, _, feature) => { features.Add(feature); return false; };
            typeof(ChannelRouter).GetMethod("BindOutboundAuthorization", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(router, new object[] { deny });
            using var scope = PlatformSourceExecution.Enter(Lease(router, source));
            Require(!(await router.SendImageWithReceiptAsync(true, Channels.AliasBase + 1, new byte[] { 1 })).Ok, "image authorization bypassed");
            Require(!(await router.SendVoiceWithReceiptAsync(true, Channels.AliasBase + 1, "https://example.com/synthetic")).Ok, "voice authorization bypassed");
            Require(!(await ((IQqChatSource)router).SendMusicWithReceiptAsync(true, Channels.AliasBase + 1, "163", "10001")).Ok, "music authorization bypassed");
            Require(fake.Calls == 0 && features.SequenceEqual(new[] { "image", "voice", "music" }), "media feature gates changed");
        });
    }
    public class MediaSource : DispatchProxy
    {
        public SendResult Result { get; set; } = new(true);
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name ?? "";
            if (name == "get_Channel") return Channels.Official;
            if (name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal)) return null;
            if (name is "SendImageWithReceiptAsync" or "SendVoiceWithReceiptAsync" or "SendMusicWithReceiptAsync") { Calls++; return Task.FromResult(Result); }
            throw new InvalidOperationException("unexpected_media_source_call");
        }
    }
}
