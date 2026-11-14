using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Persistence;
using BotAgent.Platforms;
using BotAgent.Platforms.Net;
using BotAgent.Services.Official;
using BotAgent.Services.Qq;

internal static class ConnectionChecks
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Ready(OfficialBotGateway gateway)
        => typeof(OfficialBotGateway).GetMethod("HandleDispatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(gateway, ["READY", new JsonObject { ["session_id"] = "synthetic-session" }, null]);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        foreach (var atGateway in new[] { false, true })
        {
            await test("connection: request timeout retries at " + (atGateway ? "gateway" : "token"), async () =>
            {
                using var f = new Fixture(atGateway, timeout: true);
                Ready(f.Gateway);
                await f.Gateway.StartAsync();
                await f.Http.Retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(f.Http.Attempts == 2 && !f.Gateway.IsConnected, "timeout stopped retry or retained connected state");
                Require(f.Gateway.LastErrorCode == "connection_timeout", "timeout diagnostic missing");
                Ready(f.Gateway);
                Require(f.Gateway.IsConnected && f.Gateway.LastErrorCode is null, "READY retained stale connection error");
                await f.Gateway.StopAsync();
                Require(f.Http.Attempts == 2 && !f.Gateway.IsConnected, "stop retried or retained connected state");
            });
        }
        await test("connection: lifecycle cancellation stops without retry", async () =>
        {
            using var f = new Fixture(false, timeout: false, failFirst: false);
            await f.Gateway.StartAsync();
            await f.Http.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await f.Gateway.StartAsync(); // Starting an active gateway must not create another loop.
            await f.Gateway.StopAsync();
            Require(f.Http.Attempts == 1 && !f.Gateway.IsConnected && f.Gateway.LastErrorCode is null, "lifecycle stop treated as connection failure");
        });
        await test("connection: failure retries with safe diagnostic only", async () =>
        {
            using var f = new Fixture(false, timeout: false);
            await f.Gateway.StartAsync();
            await f.Http.Retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(f.Gateway.LastErrorCode == "connection_failed", "connection failure diagnostic missing");
            Require(f.Logs.All(line => !line.Contains("synthetic-sensitive-body", StringComparison.Ordinal)), "exception body entered log");
            await f.Gateway.StopAsync();
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SyntheticDirectory _directory = new();
        public ConnectionHttp Http { get; }
        public OfficialBotGateway Gateway { get; }
        public List<string> Logs { get; } = new();
        public Fixture(bool atGateway, bool timeout, bool failFirst = true)
        {
            Http = new(atGateway, timeout, failFirst);
            Gateway = new(new PlatformSettingsBox(new PlatformOptions { OfficialEnabled = true, OfficialAppId = "app-A",
                OfficialAppSecret = "synthetic-secret", OfficialTokenUrl = "https://example.com/token",
                OfficialApiBase = "https://example.com" }), Http, () => new OfficialIdentityMap(_directory.Map), Logs.Add,
                () => OfficialProbeContext.OpenLedger(_directory.Map));
        }
        public void Dispose() { Gateway.Dispose(); _directory.Dispose(); }
    }

    private sealed class ConnectionHttp(bool atGateway, bool timeout, bool failFirst) : IPlatformHttpFetcher
    {
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public int Attempts { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Retried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task<HttpResponseMessage> Attempt(CancellationToken ct)
        {
            Attempts++; Started.TrySetResult();
            if (Attempts == 1 && failFirst)
            {
                if (timeout) throw new TaskCanceledException("synthetic-sensitive-body", new TimeoutException(), new CancellationToken(true));
                throw new HttpRequestException("synthetic-sensitive-body");
            }
            if (Attempts > 1) Retried.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unexpected_uncanceled_request");
        }
        public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
            => atGateway ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"synthetic-token","expires_in":7200}""") }) : Attempt(ct);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default) => Attempt(ct);
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => Attempt(ct);
        public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new InvalidOperationException("unexpected_request");
        public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
        public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url, ct);
        public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => GetAsync(url.ToString(), ct);
    }
}
