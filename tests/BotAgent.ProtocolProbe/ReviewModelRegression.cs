using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Services.Resilience;

// Parent entry-point wiring: await ReviewModelRegression.RunAsync(Check);
internal static class ReviewModelRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await ImageDeadlineAsync(check);
        await ImageRefreshDeadlineAsync(check);
        await ImageCallerCancellationAsync(check);
        await ImageCacheAsync(check);
        await SoftFailureRecoveryAsync(check);
        await ProbeOwnershipAsync(check);
        await PersonaRoutesAsync(check);
    }

    private static async Task ImageDeadlineAsync(Action<bool, string> check)
    {
        var body = new StallingBody();
        var headers = new List<CancellationToken>();
        var http = new FakeHttp((_, token) =>
        {
            headers.Add(token);
            if (headers.Count == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://example.com/final.png");
                return Task.FromResult(redirect);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }) { Timeout = TimeSpan.FromMilliseconds(80) };
        var images = new ImageDownloader(http) { AllowPrivateHosts = false };
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await images.DownloadBytesAsync("https://example.com/image.png", safety.Token).WaitAsync(TimeSpan.FromSeconds(5));
            check(false, "B03 stalled body must be deadline-cancelled");
        }
        catch (OperationCanceledException)
        {
            check(!safety.IsCancellationRequested, "B03 image deadline ends stalled body before caller safety cancellation");
            check(body.Token.CanBeCanceled && body.Token.IsCancellationRequested && body.Disposed,
                "B03 cancelled body is disposed");
            check(headers.Count == 2 && headers.All(token => token == body.Token) && body.Token != safety.Token,
                "B03 redirect headers and body share one linked deadline");
        }
    }

    private static async Task ImageRefreshDeadlineAsync(Action<bool, string> check)
    {
        var tokens = new List<CancellationToken>();
        var body = new StallingBody();
        var calls = 0;
        var http = new FakeHttp((_, token) =>
        {
            tokens.Add(token);
            calls++;
            return Task.FromResult(calls == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }) { Timeout = TimeSpan.FromMilliseconds(80) };
        var images = new ImageDownloader(http) { AllowPrivateHosts = false };
        images.RefreshUrls = (_, token) =>
        {
            tokens.Add(token);
            return Task.FromResult<IReadOnlyList<string>>(["https://example.com/fresh.png"]);
        };
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await images.DownloadBytesAsync("https://example.com/stale.png", safety.Token, 10001).WaitAsync(TimeSpan.FromSeconds(5));
            check(false, "B03 refreshed body must be deadline-cancelled");
        }
        catch (OperationCanceledException)
        {
            check(!safety.IsCancellationRequested && tokens.Count == 3 && tokens.All(token => token == body.Token),
                "B03 refresh callback and retry cannot reset the original deadline");
        }
    }

    private static async Task ImageCallerCancellationAsync(Action<bool, string> check)
    {
        var body = new StallingBody();
        var http = new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(body) })) { Timeout = TimeSpan.FromSeconds(30) };
        var images = new ImageDownloader(http) { AllowPrivateHosts = false };
        using var caller = new CancellationTokenSource();
        var pending = images.DownloadBytesAsync("https://example.com/cancel.png", caller.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        caller.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); check(false, "B03 caller cancellation propagates"); }
        catch (OperationCanceledException)
        {
            check(body.Token.IsCancellationRequested && body.Disposed, "B03 caller cancellation preempts image deadline");
        }
    }

    private static async Task ImageCacheAsync(Action<bool, string> check)
    {
        var http = new FakeHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47, 0, 0, 0, 0]) }));
        var images = new ImageDownloader(http) { AllowPrivateHosts = false };
        const string url = "https://example.com/complete.png";
        var downloaded = await images.DownloadBytesAsync(url, CancellationToken.None);
        check(downloaded is { } image && image.Data.Length == 8 && image.Mime == "image/png" && image.Ext == "png" && http.Calls == 1,
            "B03 complete image remains available with no caller token");
        check(await images.DownloadAsDataUrl(url, CancellationToken.None) is not null && http.Calls == 1 && images.CacheHits == 1,
            "B03 successful image cache still skips network");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await images.DownloadBytesAsync(url, cancelled.Token); check(false, "B03 cached image honors cancellation"); }
        catch (OperationCanceledException) { check(http.Calls == 1, "B03 already-cancelled cache caller does not send HTTP"); }
    }

    private static async Task SoftFailureRecoveryAsync(Action<bool, string> check)
    {
        foreach (var reason in new[] { "provider_secret_unavailable", "provider_route_unavailable" })
        {
            var now = DateTimeOffset.UnixEpoch;
            var published = new List<ProviderCircuitSnapshot>();
            var runner = new ProviderFailoverRunner([new ProviderCandidate("synthetic", 0)], clock: () => now,
                failureThreshold: 1, cooldown: TimeSpan.FromSeconds(1), onSnapshotChanged: published.Add);
            await runner.RunAsync<string>((_, _) => Task.FromResult(ProviderCallResult<string>.Failure("synthetic_hard")));
            now = now.AddSeconds(2);
            var before = runner.Snapshots()[0];
            var soft = await runner.RunAsync<string>((_, _) => Task.FromResult(ProviderCallResult<string>.Failure(reason, false)));
            var released = runner.Snapshots()[0];
            check(!soft.Succeeded && soft.ReasonCode == reason && released.State == ProviderCircuitState.HalfOpen && !released.ProbeInFlight,
                "B08 " + reason + " releases owned half-open slot");
            check(released.ConsecutiveHardFailures == before.ConsecutiveHardFailures && released.CooldownUntil == before.CooldownUntil,
                "B08 soft failure does not count as hard failure or reset cooldown");
            check(published.Last() == released, "B08 released probe snapshot is published");
            var calls = 0;
            var recovered = await runner.RunAsync<string>((_, _) => { calls++; return Task.FromResult(ProviderCallResult<string>.Success("OK")); });
            check(recovered.Succeeded && calls == 1 && runner.Snapshots()[0].State == ProviderCircuitState.Closed,
                "B08 restored route recovers immediately without waiting or restart");
        }
    }

    private static async Task ProbeOwnershipAsync(Action<bool, string> check)
    {
        foreach (var completion in new[] { "soft", "cancel", "success", "hard", "timeout", "exception" })
        {
            var now = DateTimeOffset.UnixEpoch;
            var runner = new ProviderFailoverRunner([new ProviderCandidate("synthetic", 0)], clock: () => now,
                failureThreshold: 1, cooldown: TimeSpan.FromSeconds(1));
            using var caller = new CancellationTokenSource();
            var staleResult = new TaskCompletionSource<ProviderCallResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stale = runner.RunAsync<string>(async (_, token) =>
            {
                staleEntered.TrySetResult();
                return await staleResult.Task.WaitAsync(token);
            }, caller.Token);
            await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await runner.RunAsync<string>((_, _) => Task.FromResult(ProviderCallResult<string>.Failure("synthetic_hard")));
            now = now.AddSeconds(2);
            var ownerResult = new TaskCompletionSource<ProviderCallResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = runner.RunAsync<string>((_, _) => { ownerEntered.TrySetResult(); return ownerResult.Task; });
            await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try
            {
                switch (completion)
                {
                    case "cancel": caller.Cancel(); break;
                    case "timeout": staleResult.SetException(new OperationCanceledException()); break;
                    case "exception": staleResult.SetException(new InvalidOperationException("synthetic")); break;
                    default: staleResult.SetResult(completion == "success" ? ProviderCallResult<string>.Success("OK")
                        : ProviderCallResult<string>.Failure("synthetic", completion == "hard")); break;
                }
                try { await stale.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (OperationCanceledException) when (completion == "cancel") { }
                var snapshot = runner.Snapshots()[0];
                check(snapshot.State == ProviderCircuitState.HalfOpen && snapshot.ProbeInFlight && snapshot.ConsecutiveHardFailures == 1,
                    "B08 stale closed caller " + completion + " cannot mutate another admitted probe");
                var extraCalls = 0;
                await runner.RunAsync<string>((_, _) => { extraCalls++; return Task.FromResult(ProviderCallResult<string>.Success("OK")); });
                check(extraCalls == 0, "B08 only the current owner can hold half-open admission after " + completion);
            }
            finally
            {
                ownerResult.TrySetResult(ProviderCallResult<string>.Success("OK"));
                await owner.WaitAsync(TimeSpan.FromSeconds(3));
                caller.Cancel();
            }
            check(runner.Snapshots()[0].State == ProviderCircuitState.Closed, "B08 current owner can restore after stale " + completion);
        }

        var time = DateTimeOffset.UnixEpoch;
        var cancelledRunner = new ProviderFailoverRunner([new ProviderCandidate("synthetic", 0)], clock: () => time,
            failureThreshold: 1, cooldown: TimeSpan.FromSeconds(1));
        await cancelledRunner.RunAsync<string>((_, _) => Task.FromResult(ProviderCallResult<string>.Failure("synthetic")));
        time = time.AddSeconds(2);
        using var cancelOwner = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledOwner = cancelledRunner.RunAsync<string>(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ProviderCallResult<string>.Success("unreachable");
        }, cancelOwner.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancelOwner.Cancel();
        try { await cancelledOwner.WaitAsync(TimeSpan.FromSeconds(3)); check(false, "B08 owner cancellation propagates"); }
        catch (OperationCanceledException) { check(!cancelledRunner.Snapshots()[0].ProbeInFlight, "B08 cancelled owner releases its own probe"); }
        check((await cancelledRunner.RunAsync<string>((_, _) => Task.FromResult(ProviderCallResult<string>.Success("OK")))).Succeeded,
            "B08 next probe admitted after owner cancellation");
    }

    private static async Task PersonaRoutesAsync(Action<bool, string> check)
    {
        foreach (var protocol in new[] { ModelProtocol.OpenAiChat, ModelProtocol.Ollama })
        {
            var route = new ModelProviderRoute("synthetic", "https://example.com/v1", "example-model",
                protocol == ModelProtocol.Ollama ? "" : "synthetic-key", protocol);
            var seenRoute = false;
            var http = new FakeHttp(async (request, token) =>
            {
                var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                seenRoute = payload["model"]!.GetValue<string>() == route.ModelName
                    && (protocol == ModelProtocol.Ollama ? request.Headers.Authorization is null : request.Headers.Authorization?.Parameter == "synthetic-key");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(protocol == ModelProtocol.Ollama
                    ? """{"message":{"content":"Synthetic summary"},"done":true}"""
                    : """{"choices":[{"message":{"content":"Synthetic summary"}}]}""") };
            });
            var options = new ModelOptions { ApiKey = "" };
            var images = new ImageDownloader(http) { AllowPrivateHosts = false };
            var transport = new ModelTransport(() => options, http, http, images, http.Timeout);
            var runner = new ProviderFailoverRunner([new ProviderCandidate(route.ProviderId, 0)], clock: () => DateTimeOffset.UnixEpoch);
            ModelProviderRoute? resolved = route;
            var client = new OpenAiClient(() => options, http, http, images, transport, runner, providerRouteResolver: _ => resolved);
            check(await client.SummarizePersonaAsync("Synthetic member", null, ["Synthetic statement"], 64) == "Synthetic summary"
                && http.Calls == 1 && seenRoute, "B23 " + protocol + " persona uses registered route without legacy key");
            check(await client.SummarizePersonaAsync("Synthetic member", null, [], 64) is null && http.Calls == 1,
                "B23 empty persona input still avoids network");
            resolved = null;
            check(await client.SummarizePersonaAsync("Synthetic member", null, ["Synthetic statement"], 64) is null && http.Calls == 1,
                "B23 missing registered route fails closed without legacy fallback");
            resolved = route;
            check(await client.SummarizePersonaAsync("Synthetic member", null, ["Synthetic statement"], 64) == "Synthetic summary" && http.Calls == 2,
                "B23 restored managed route remains available");
            var legacy = new OpenAiClient(() => options, http, http, images, transport);
            check(await legacy.SummarizePersonaAsync("Synthetic member", null, ["Synthetic statement"], 64) is null && http.Calls == 2,
                "B23 unconfigured legacy persona still fails closed");
        }
    }

    private sealed class StallingBody : Stream
    {
        internal CancellationToken Token { get; private set; }
        internal bool Disposed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Token = ct;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
