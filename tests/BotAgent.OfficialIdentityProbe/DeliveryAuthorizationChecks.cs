using System.Text.Json.Nodes;
using BotAgent.Adapters.Official;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Reply;
using BotAgent.Services.Qq;

internal static partial class RuntimeDeliveryChecks
{
    public static async Task RunAuthorizationAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        foreach (var group in new[] { true, false })
        {
            var label = group ? "group" : "C2C";
            foreach (var point in new[] { "download", "upload", "credential", "upload-write", "message-write" })
                await test($"delivery authorization {label} revocation during {point} prevents the next HTTP effect", async () =>
                {
                    using var f = new Fixture(clock, group); f.Receive("first");
                    using var source = PlatformSourceExecution.Enter(f.Lease("first"));
                    var allowed = true;
                    using var guard = ReplyCommitBoundary.Enter(new AuthorizationGuard());
                    using var validation = ReplyCommitBoundary.EnterValidation(() =>
                    { if (!allowed) throw new OperationCanceledException("synthetic_authorization_revoked"); });
                    f.Http.DownloadResponse = _ => { if (point == "download") allowed = false; return Reply(200, "synthetic-audio"); };
                    f.Http.BeforeUpload = () => { if (point is "upload" or "credential") allowed = false; return Task.CompletedTask; };
                    if (point == "credential") f.Http.UploadResponse = _ => Reply(401, "{\"code\":11244}");
                    f.AfterCommit = snapshot =>
                    {
                        var rows = JsonNode.Parse(snapshot)!["Operations"]!.AsObject();
                        if (rows.Any(row => row.Value!["State"]!.GetValue<int>() == (int)(point == "upload-write"
                                ? OfficialDeliveryState.Uploading : OfficialDeliveryState.Attempting))
                            && point is "upload-write" or "message-write") allowed = false;
                    };
                    SendResult? receipt = null;
                    try
                    {
                        receipt = point == "message-write" ? await f.Send()
                            : await f.Gateway.SendVoiceWithReceiptAsync(group, f.Alias, "https://example.com/speak");
                    }
                    catch (OperationCanceledException) when (!allowed) { }
                    Require(!allowed, "revocation hook did not execute");
                    Require(receipt?.Ok != true && f.Http.Messages.Count == 0
                        && f.Http.Uploads == (point is "upload" or "credential" ? 1 : 0),
                        $"revoked operation escaped: uploads={f.Http.Uploads}, messages={f.Http.Messages.Count}");
                    if (point.EndsWith("-write", StringComparison.Ordinal))
                    {
                        var row = f.Rows().Single();
                        Require(row["Status"]!.GetValue<int>() == (int)DeliveryStatus.Rejected,
                            "no-HTTP denial became an unknown delivery");
                        f.Restart();
                        Require(f.Rows().Single()["Status"]!.GetValue<int>() == (int)DeliveryStatus.Rejected,
                            "restart changed known rejection");
                    }
                });
            await test($"delivery authorization {label} upload validates without committing the first reply", async () =>
            {
                using var f = new Fixture(clock, group); f.Receive("first");
                using var source = PlatformSourceExecution.Enter(f.Lease("first"));
                var boundary = new AuthorizationGuard(); var checks = 0;
                using var guard = ReplyCommitBoundary.Enter(boundary);
                using var validation = ReplyCommitBoundary.EnterValidation(() => checks++);
                f.Http.BeforeUpload = () =>
                {
                    Require(checks > 0 && boundary.Commits == 0 && boundary.Effects == 0,
                        "upload skipped validation or committed reply scheduling");
                    return Task.CompletedTask;
                };
                Require((await f.Gateway.SendImageWithReceiptAsync(group, f.Alias, [1, 2, 3])).Ok
                    && f.Http.Uploads == 1 && f.Http.Messages.Count == 1 && boundary.Commits == 1,
                    "valid media did not commit exactly one reply");
            });
        }
    }

    private sealed class AuthorizationGuard : IReplyCommitGuard
    {
        public int Commits { get; private set; }
        public int Effects { get; private set; }
        public Task BeforeSendAsync() { Commits++; return Task.CompletedTask; }
        public Task BeforeEffectAsync() { Effects++; return Task.CompletedTask; }
        public Task BeforePrepareAsync() => Task.CompletedTask;
        public bool TryBeforeSend() { Commits++; return true; }
    }
}
