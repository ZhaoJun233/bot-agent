using System.Reflection;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Stickers;
using BotAgent.Services.Qq;
using BotAgent.Services.Reply;
using BotAgent.Services.Stickers;

static class OutcomeChecks
{
    public static async Task RunAsync(Action<string, bool> check, Func<SyntheticSource, PlainSender> sender)
    {
        var failures = 0;
        void Check(string label, bool value)
        {
            try { check(label, value); }
            catch (InvalidOperationException) { failures++; Console.WriteLine("OUTCOME_FAIL " + label); }
        }
        var failuresToSend = new[]
        {
            DeliveryResult.Unknown("synthetic_unknown"), DeliveryResult.Throttled(7),
            DeliveryResult.Rejected("synthetic_limit"), DeliveryResult.Permanent("synthetic_forbidden"),
            DeliveryResult.Transient("synthetic_unavailable"),
        };
        foreach (var failure in failuresToSend)
        {
            foreach (var prefix in new[] { false, true })
            {
                var source = new SyntheticSource { TextResult = call => prefix && call == 1
                    ? new(true) : new(false) { Delivery = failure } };
                var report = await sender(source).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。thirdpart。", null);
                Check($"cadence retains {failure.Status} after confirmed={prefix}",
                    report.Delivery == failure && report.SentSegments.Count == (prefix ? 1 : 0)
                    && source.TextCalls == (prefix ? 2 : 1) && !report.AllSent);
            }
        }
        var contradictory = new SyntheticSource { TextResult = _ => new(true, 10002) { Delivery = failuresToSend[0] } };
        var contradiction = await sender(contradictory).SendWithCadenceAsync(true, 10001, "firstpart。secondpart。", null);
        Check("unknown receipt cannot be acknowledged through legacy true", !contradiction.AnySent && !contradiction.AllSent && contradictory.TextCalls == 1);

        var unknownText = new BotAgent.Services.Ports.CadenceSendReport(new[] { "confirmed" }, "synthetic_unknown") { Delivery = failuresToSend[0] };
        var unknownChat = ChatDeliveryReport.FromAcknowledgements(unknownText, true, Array.Empty<string>(), false, Array.Empty<string>());
        Check("public acknowledgment report retains unknown", unknownChat.Delivery == failuresToSend[0] && unknownChat.AnyChatSent && !unknownChat.CompleteChatReply);
        var finish = typeof(ChatDeliveryReport).Assembly.GetType("BotAgent.Services.Reply.ReplyDeliveryResults")!
            .GetMethod("Finish", BindingFlags.Static | BindingFlags.NonPublic)!;
        ChatDeliveryReport Finish(bool any, bool all, params DeliveryResult?[] receipts)
            => (ChatDeliveryReport)finish.Invoke(null, new object[] { new ChatDeliveryReport(any, any && all), any, all, receipts })!;
        Check("no confirmed output cannot report success", Finish(false, true).Delivery is { IsSuccess: false });
        var unknown = Finish(true, false, DeliveryResult.Ok("10002"), failuresToSend[0]);
        Check("unknown dominates confirmed prefix", unknown.AnyChatSent && !unknown.CompleteChatReply && unknown.Delivery is { Status: DeliveryStatus.Unknown, MessageId: null });
        var partial = Finish(true, false, DeliveryResult.Ok("10002"), failuresToSend[2]);
        Check("known rejection after prefix is partial", partial.Delivery is { Status: DeliveryStatus.PartialSuccess, ReasonCode: "synthetic_limit", MessageId: null } && !partial.CompleteChatReply);
        var throttle = Finish(true, false, DeliveryResult.Ok(), failuresToSend[1]);
        Check("partial throttle retains retry interval", throttle.Delivery is { Status: DeliveryStatus.PartialSuccess, RetryAfterSeconds: 7 });
        Check("zero confirmed throttle remains throttled", Finish(false, false, failuresToSend[1]).Delivery is { Status: DeliveryStatus.Throttled, RetryAfterSeconds: 7 });
        Check("confirmed degradation remains explicit", Finish(true, true, DeliveryResult.Degraded("10002", "synthetic_media")).Delivery is { Status: DeliveryStatus.Degraded });

        var store = DispatchProxy.Create<IStickerRepository, OutcomeStickerStore>();
        var item = new StickerRecord { Id = "synthetic-image" };
        foreach (var receipt in new[]
        {
            new SendResult(false) { Delivery = failuresToSend[0] },
            new SendResult(true) { Delivery = failuresToSend[0] },
            new SendResult(false) { Delivery = failuresToSend[2] },
            new SendResult(true) { Delivery = DeliveryResult.Degraded("10002", "synthetic_image") },
        })
        {
            var source = new SyntheticSource { ImageResult = () => receipt };
            using var service = new StickerService(store, null!, source, new TestSettings(), _ => { });
            var result = await service.SendWithReceiptAsync(true, 10001, item, null, "group:10001");
            var confirmed = receipt.Ok && receipt.Delivery!.IsSuccess;
            Check("image receipt preserves status and only confirmed image enters cooldown " + receipt.Delivery!.Status + receipt.Ok,
                result.Ok == confirmed && result.Delivery == receipt.Delivery && source.ImageCalls == 1
                && service.Allow("group:10001", item.Id, out _) != confirmed);
        }
        var throwing = new SyntheticSource { ImageResult = () => throw new HttpRequestException("synthetic-body") };
        using (PlatformSourceExecution.Enter(new(new PacingOwner(), throwing,
            new(new(PlatformId.QqOfficial, AccountScope.Legacy, "synthetic-image-driver"), null), CancellationToken.None, () => true)))
        using (var service = new StickerService(store, null!, throwing, new TestSettings(), _ => { }))
        {
            Check("official post-call image exception remains unknown",
                (await service.SendWithReceiptAsync(true, 10001, item, null, "official:group:10001")).Delivery?.Status == DeliveryStatus.Unknown);
            ((OutcomeStickerStore)(object)store).FailRead = true;
            Check("image preparation failure has no extra transport",
                (await service.SendWithReceiptAsync(true, 10001, item, null, "official:group:10001")).Delivery?.Status == DeliveryStatus.Rejected && throwing.ImageCalls == 1);
        }
        if (failures > 0) throw new InvalidOperationException("outcome checks failed=" + failures);
    }
}

public class OutcomeStickerStore : DispatchProxy
{
    public bool FailRead { get; set; }
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == "ReadBytesAsync"
            ? FailRead ? throw new IOException("synthetic-read-failure") : Task.FromResult(new byte[] { 1, 2, 3 })
            : throw new InvalidOperationException("unexpected_sticker_repository_call");
}
