using System.Net;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    private const string SyntheticPanelToken = "synthetic-integration-panel-token";
    private const string SyntheticPanelPassword = "synthetic-panel-password-123";

    // Authentication is explicit at call sites; HttpGetAsync/PostJsonAsync remain anonymous by default.
    private static Task<(int Status, string Body)> PanelGetAsync(string url, string panelToken = SyntheticPanelToken)
        => HttpGetAsync(url, panelToken);

    private static Task<(int Code, string Body)> PanelPostJsonAsync(string url, string json, string panelToken = SyntheticPanelToken)
        => PostJsonAsync(url, json, panelToken);

    private static HttpClient CreatePanelHttpClient(int panelPort, int timeoutSeconds, string panelToken = SyntheticPanelToken)
        => new(new PanelFixtureAuthHandler(panelPort, panelToken))
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };

    // A test credential must not reach a model/mock service or follow a redirect to another authority.
    private sealed class PanelFixtureAuthHandler : DelegatingHandler
    {
        private readonly Uri _panelOrigin;
        private readonly string _panelToken;

        public PanelFixtureAuthHandler(int panelPort, string panelToken)
            : base(new HttpClientHandler { AllowAutoRedirect = false })
        {
            _panelOrigin = new Uri($"http://127.0.0.1:{panelPort}");
            _panelToken = panelToken;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (uri is null || uri.Scheme != _panelOrigin.Scheme || uri.Host != _panelOrigin.Host || uri.Port != _panelOrigin.Port)
                throw new InvalidOperationException("Authenticated test client may only contact its loopback panel origin.");

            request.Headers.Add("X-Panel-Token", _panelToken);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
