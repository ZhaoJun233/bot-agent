using BotAgent.Services;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Model;

/// <summary>Preserves the legacy constructor while the Model module owns transport behavior.</summary>
internal sealed class ModelTransport : BotAgent.Model.ModelTransport
{
    public ModelTransport(SettingsBox box, IHttpFetcher chatHttp, IHttpFetcher auxHttp,
        ImageDownloader imageDownloader, TimeSpan chatTimeout)
        : base(HostModelOptions.CreateAccessor(box), chatHttp, auxHttp, imageDownloader, chatTimeout) { }
}
