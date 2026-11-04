using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Services.Resilience;

internal static class TimeoutChecks
{
    internal static async Task RunAsync(Action<bool, string> check, JsonObject payload)
    {
        var route = new ModelProviderRoute("synthetic", "https://example.com/v1", "example-model", "synthetic-key", Options: new(TimeoutSeconds: 1));
        foreach (var invalid in new[] { -1, 0, 301 })
        {
            var bad = route with { Options = new(TimeoutSeconds: invalid) };
            var http = new FakeHttp((_, _) => Task.FromResult(Response()));
            check((await new ProviderConnectionTester(http).TestAsync(bad)).ErrorCode == "model_timeout_invalid" && http.Calls == 0, "tester rejects invalid provider deadline before network");
            check((await new ProviderModelDiscovery(http).ListAsync(bad)).ErrorCode == "model_timeout_invalid" && http.Calls == 0, "discovery rejects invalid provider deadline before network");
            try { await Transport(http).SendAsync(payload, 0, [], bad, default); check(false, "invalid transport deadline rejected"); }
            catch (ModelFault fault) { check(fault.ErrorCode == "model_timeout_invalid" && http.Calls == 0, "transport rejects invalid deadline without retry"); }
            check(await Client(http, bad).CompleteChatWithReasoningAsync("example-model", "Synthetic", [("user", "Synthetic")], 64, 0, null) is null && http.Calls == 0, "auxiliary invalid deadline fails closed");
        }
        var testerHttp = Slow();
        var discoveryHttp = Slow();
        var chatHttp = Slow();
        var auxiliaryHttp = Slow();
        var payloadHttp = Slow();
        var tester = new ProviderConnectionTester(testerHttp).TestAsync(route);
        var discovery = new ProviderModelDiscovery(discoveryHttp).ListAsync(route);
        var chat = Transport(chatHttp).SendAsync(payload, 0, [], route, default);
        var auxiliary = Client(auxiliaryHttp, route).CompleteChatWithReasoningAsync("example-model", "Synthetic", [("user", "Synthetic")], 64, 0, null);
        var auxPayload = (Task<string?>)typeof(OpenAiClient).GetMethod("SendAuxPayloadCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Client(payloadHttp, route), [payload, CancellationToken.None, false])!;
        check((await tester.WaitAsync(TimeSpan.FromSeconds(8))).ErrorCode == "model_timeout" && testerHttp.Calls == 1, "tester uses provider deadline instead of300s network budget");
        check((await discovery.WaitAsync(TimeSpan.FromSeconds(8))).ErrorCode == "model_timeout" && discoveryHttp.Calls == 1, "discovery provider deadline is single total budget");
        try { await chat.WaitAsync(TimeSpan.FromSeconds(8)); check(false, "transport should time out"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_timeout" && chatHttp.Calls == 2, "provider deadline retains exactly one transport timeout retry"); }
        check(await auxiliary.WaitAsync(TimeSpan.FromSeconds(8)) is null && auxiliaryHttp.Calls == 1, "registered auxiliary completion honors provider deadline");
        check(await auxPayload.WaitAsync(TimeSpan.FromSeconds(8)) is null && payloadHttp.Calls == 1, "registered auxiliary payload honors provider deadline");

        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledHttp = new FakeHttp(async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(); }) { Timeout = TimeSpan.FromSeconds(300) };
        var cancelledCall = Transport(cancelledHttp).SendAsync(payload, 0, [], route with { Options = new(TimeoutSeconds: 300) }, caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        caller.Cancel();
        try { await cancelledCall; check(false, "caller cancellation should propagate"); }
        catch (OperationCanceledException) { check(cancelledHttp.Calls == 1, "outer cancellation preempts longer deadline with no retry"); }

        foreach (var seconds in new int?[] { null, 300 })
        {
            var linked = false;
            var http = new FakeHttp((_, token) => { linked = token.CanBeCanceled; return Task.FromResult(Response()); });
            check((await new ProviderConnectionTester(http).TestAsync(route with { Options = new(TimeoutSeconds: seconds) })).Succeeded && linked, "nullable/default and max provider budgets keep cancellable requests");
        }
    }

    private static FakeHttp Slow() => new(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Response(); }) { Timeout = TimeSpan.FromSeconds(300) };
    private static ModelTransport Transport(FakeHttp http) => new(() => new ModelOptions(), http, http, new ImageDownloader(http), http.Timeout);
    private static OpenAiClient Client(FakeHttp http, ModelProviderRoute route) => new(() => new ModelOptions(), http, http, new ImageDownloader(http), Transport(http),
        new ProviderFailoverRunner([new ProviderCandidate(route.ProviderId, 0)]), new Dictionary<string, ModelProviderRoute> { [route.ProviderId] = route });
    private static HttpResponseMessage Response() => new(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"choices":[{"message":{"content":"OK"}}]}""") };
}
