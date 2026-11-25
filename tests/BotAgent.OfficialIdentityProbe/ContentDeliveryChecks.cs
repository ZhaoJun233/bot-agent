using System.Net;
using System.Text.Json;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    public static async Task RunContentAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        await test("content ordinary text keeps success and confirmed text", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var sent = await f.Send();
            Require(sent.Ok && sent.Delivery?.Status == DeliveryStatus.Success && sent.DeliveredText == "synthetic-output", "ordinary receipt changed or lost text");
        });
        await test("content unknown domain is durably degraded and preserves exact sent text", async () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                var sent = await f.Gateway.SendTextAsync(group, f.Alias, "synthetic-content https://example.com/path?token=test-token-synthetic");
                var body = f.Http.Messages.Single()["content"]!.GetValue<string>();
                Require(sent.Ok && sent.Delivery?.Status == DeliveryStatus.Degraded && sent.Delivery.ReasonCode == "link_domain_unverified", "stripped link reported ordinary success");
                Require(sent.MessageId > 0 && sent.DeliveredText == body && !body.Contains("https://"), "confirmed text does not match HTTP");
                Require(f.Rows().Single()["Reason"]!.GetValue<string>() == "link_domain_unverified", "degradation missing from durable receipt");
                f.Restart();
                Require(f.Rows().Single()["Status"]!.GetValue<int>() == (int)DeliveryStatus.Degraded, "restart lost degradation");
                Require(!File.ReadAllText(f.LedgerPath).Contains("synthetic-content"), "receipt snapshot contains body");
            }
        });
        await test("content link and missing quote preserve both reasons", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var sent = await f.Gateway.SendTextAsync(true, f.Alias, "https://example.com", replyToMessageId: f.Inbound.Single().MessageId);
            Require(sent.Delivery?.Status == DeliveryStatus.Degraded && sent.Delivery.ReasonCode == "link_reference_degraded", "quote overwrote content degradation");
            Require(f.Rows().Single()["Reason"]!.GetValue<string>() == sent.Delivery!.ReasonCode, "combined reason not persisted");
        });
        await test("content unknown and rejected sends never become successful degradation", async () =>
        {
            foreach (var status in new[] { 400, 429, 503 })
            {
                using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
                f.Http.MessageResponse = _ => Reply(status, "{\"code\":304003,\"message\":\"synthetic\"}");
                var sent = await f.Gateway.SendTextAsync(true, f.Alias, "https://example.com");
                Require(!sent.Ok && sent.Delivery is { IsSuccess: false } && sent.DeliveredText is null, "failed send claimed confirmed text");
                Require(f.Http.Messages.Count == 1 && f.Rows().Single()["Status"]!.GetValue<int>() == (int)sent.Delivery!.Status, "failed status or attempt count changed");
            }
        });
        await test("content ambiguous receipt commit stays unknown without confirmed text", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.AfterCommit = snapshot => { if (States(snapshot).Contains(BotAgent.Adapters.Official.OfficialDeliveryState.Completed)) throw new IOException("synthetic-commit"); };
            var sent = await f.Gateway.SendTextAsync(true, f.Alias, "https://example.com");
            Require(sent.Delivery?.Status == DeliveryStatus.Unknown && sent.DeliveredText is null && f.Http.Messages.Count == 1, "ambiguous commit masked by degradation");
        });
        await test("content music fallback exposes and persists a structured degraded receipt", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var sent = await ((IQqChatSource)f.Gateway).SendMusicWithReceiptAsync(true, f.Alias, "163", "10001", "synthetic-song");
            Require(sent.Ok && sent.Delivery?.Status == DeliveryStatus.Degraded && sent.Delivery.ReasonCode == "music_card_unavailable", "music fallback lost structured degradation");
            Require(sent.DeliveredText == f.Http.Messages.Single()["content"]!.GetValue<string>(), "music text missing");
            Require(f.Rows().Single()["Reason"]!.GetValue<string>() == "music_card_unavailable", "music reason not persisted");
        });
        await test("content music title link preserves composite degradation", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            var sent = await ((IQqChatSource)f.Gateway).SendMusicWithReceiptAsync(true, f.Alias, "163", "10001", "synthetic https://example.com");
            Require(sent.Delivery?.ReasonCode == "music_link_degraded" && !sent.DeliveredText!.Contains("https://"), "music link cause lost");
        });
        await test("content music unknown receipt remains unknown", async () =>
        {
            using var f = new Fixture(clock); f.Receive("first"); using var execution = PlatformSourceExecution.Enter(f.Lease("first"));
            f.Http.BeforeMessage = _ => throw new HttpRequestException("synthetic-unknown");
            var sent = await ((IQqChatSource)f.Gateway).SendMusicWithReceiptAsync(true, f.Alias, "163", "10001", "synthetic-song");
            Require(!sent.Ok && sent.Delivery?.Status == DeliveryStatus.Unknown && sent.DeliveredText is null && f.Http.Messages.Count == 1, "music collapsed unknown into bool");
        });
        await test("content receipt diagnostics and JSON omit delivered text", () =>
        {
            var sent = new SendResult(true) { DeliveredText = "synthetic-private-content" };
            Require(!sent.ToString().Contains("synthetic-private-content") && !JsonSerializer.Serialize(sent).Contains("synthetic-private-content"), "receipt diagnostic contains text");
            return Task.CompletedTask;
        });
    }
}
