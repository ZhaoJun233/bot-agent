using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Domain.Model;
using BotAgent.Model;
using BotAgent.Services;

internal static class TransportChecks
{
    internal static async Task RunAsync(Action<bool, string> check, JsonObject payload, IReadOnlyDictionary<ModelProtocol, string> replies)
    {
        var clock = new FakeClock();
        ModelClock.Initialize(clock);
        ModelLogging.WriteHandler = (_, _) => { };
        ModelLogging.WarnHandler = (_, _) => { };
        foreach (var (protocol, reply) in replies)
        {
            var route = new ModelProviderRoute("synthetic", "https://example.com/v1", "example-model", protocol == ModelProtocol.Ollama ? "" : "synthetic-key", protocol);
            var http = new FakeHttp((_, _) => Task.FromResult(Response(reply)));
            var transport = Transport(http);
            var outcome = await transport.SendAsync(payload, 1, [10001], route, CancellationToken.None);
            check(ModelJson.HasChoices(outcome.Json) && http.Calls == 1, protocol + " transport normalized");
            check((await new ProviderConnectionTester(http).TestAsync(route)).Succeeded, protocol + " tester normalized");
            var malformed = new FakeHttp((_, _) => Task.FromResult(Response("[]")));
            check(!(await new ProviderConnectionTester(malformed).TestAsync(route)).Succeeded, protocol + " tester rejects malformed");
        }
        var native = new ModelProviderRoute("synthetic", "https://example.com/v1", "example-model", "synthetic-key", ModelProtocol.AnthropicMessages);
        var attempts = 0;
        var transient = new FakeHttp((_, _) => Task.FromResult(++attempts == 1 ? Response("", HttpStatusCode.TooManyRequests) : Response(replies[native.Protocol])));
        check(ModelJson.HasChoices((await Transport(transient).SendAsync(payload, 0, [], native, default)).Json) && attempts == 2, "429 retried once");
        var alwaysFail = new FakeHttp((_, _) => Task.FromResult(Response("synthetic untrusted body", HttpStatusCode.ServiceUnavailable)));
        try { await Transport(alwaysFail).SendAsync(payload, 0, [], native, default); check(false, "503 fails"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_upstream_error" && alwaysFail.Calls == 2 && !fault.ToString().Contains("synthetic untrusted body"), "503 bounded safe failure"); }
        var unauthorized = new FakeHttp((_, _) => Task.FromResult(Response("", HttpStatusCode.Unauthorized)));
        try { await Transport(unauthorized).SendAsync(payload, 0, [], native, default); check(false, "401 fails"); }
        catch (ModelFault fault) { check(fault.ErrorCode == "model_auth_failed" && unauthorized.Calls == 1, "401 no retry"); }
        var stripped = false;
        var emptyAttempts = 0;
        var imageFallback = new FakeHttp(async (request, ct) =>
        {
            emptyAttempts++;
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (emptyAttempts == 3) stripped = !body.Contains("AA==") && body.Contains("Synthetic prompt");
            return Response(emptyAttempts < 3 ? """{"content":[]}""" : replies[native.Protocol]);
        });
        var fallback = await Transport(imageFallback).SendAsync(payload, 1, [10001], native, default);
        check(fallback.TextOnlyRetry && stripped && imageFallback.Calls == 3, "native empty response and image stripping fallback");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var cancelledHttp = new FakeHttp((_, _) => Task.FromResult(Response(replies[native.Protocol])));
        try { await Transport(cancelledHttp).SendAsync(payload, 0, [], native, cancel.Token); check(false, "cancelled call fails"); }
        catch (OperationCanceledException) { check(cancelledHttp.Calls == 0, "caller cancellation no network no retries"); }
        var oversized = new FakeHttp((_, _) => Task.FromResult(Response(new string(' ', 65537))));
        check((await new ProviderConnectionTester(oversized).TestAsync(native)).ErrorCode == "model_response_too_large", "tester size bound preserved");
        check(clock.Delays > 0, "retry uses injected clock");
    }
    private static ModelTransport Transport(FakeHttp http) => new(() => new ModelOptions(), http, http, new ImageDownloader(http), http.Timeout);
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
}
