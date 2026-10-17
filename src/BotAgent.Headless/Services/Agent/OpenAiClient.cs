using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Services.Resilience;

namespace BotAgent.Services.Agent;

/// <summary>Compatibility facade; model algorithms and transport belong to the Model module.</summary>
public sealed class OpenAiClient : BotAgent.Model.OpenAiClient
{
    public OpenAiClient(
        SettingsBox box, IHttpFetcher auxHttp, IHttpFetcher chatHttp, IImageDownloader images, IModelTransport transport,
        ProviderFailoverRunner? providerFailover = null,
        IReadOnlyDictionary<string, ModelProviderRoute>? providerRoutes = null,
        Func<string, ModelProviderRoute?>? providerRouteResolver = null)
        : base(HostModelOptions.CreateAccessor(box), auxHttp, chatHttp, images, transport,
            providerFailover, providerRoutes, providerRouteResolver) { }
}
