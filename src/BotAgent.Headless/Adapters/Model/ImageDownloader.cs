namespace BotAgent.Adapters.Model;

/// <summary>Preserves the host type used by existing callers and reflection-based probes.</summary>
internal sealed class ImageDownloader : BotAgent.Model.ImageDownloader
{
    public ImageDownloader(IHttpFetcher http) : base(http)
        => BotAgent.Adapters.Time.ClockBindings.InitializeModel();
}
