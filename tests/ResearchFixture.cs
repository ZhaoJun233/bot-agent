using System.Reflection;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Plugins;
using BotAgent.Model;
using BotAgent.Services;
using BotAgent.Services.Net;

/// <summary>Tests exercise the host's actual projections without exposing them as plugin APIs.</summary>
internal static class ResearchFixture
{
    internal static Func<ResearchOptions> Options(Func<AppSettings> settings)
        => ((Func<Func<AppSettings>, Func<ResearchOptions>>)typeof(PluginPackageService).Assembly
            .GetType("BotAgent.Services.Net.HostResearchOptions")!.GetMethod("CreateAccessor", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<Func<AppSettings>, Func<ResearchOptions>>)))(settings);

    internal static GeminiGroundedTextModel Model(IHttpFetcher http, Func<AppSettings> settings)
    {
        var project = (Func<AppSettings, ModelOptions>)typeof(PluginPackageService).Assembly
            .GetType("BotAgent.Services.Agent.HostModelOptions")!.GetMethod("Project", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Func<AppSettings, ModelOptions>));
        return new(http, () => project(settings()));
    }
}
