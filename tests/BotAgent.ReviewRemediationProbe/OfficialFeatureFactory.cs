using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Profiles;
using BotAgent.Services.Stickers;
using BotAgent.Services.Voice;
using BotAgent.TestFixtures;

namespace BotAgent.ReviewRemediationProbe;

/// <summary>Loads official feature plugins from bot-agent-plugins into isolated load contexts bound to host contracts.</summary>
internal static class OfficialFeatureFactory
{
    private static readonly string MarketplaceRoot =
        OfficialPluginsTestLocator.ResolveMarketplaceRoot(OfficialPluginsTestLocator.FindRepoRoot());

    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Stickers =
        OfficialPluginsTestLocator.LoadIsolatedPlugin(MarketplaceRoot, "stickers", "BotAgent.Package.Stickers");
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Profiles =
        OfficialPluginsTestLocator.LoadIsolatedPlugin(MarketplaceRoot, "profiles", "BotAgent.Package.Profiles");
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Voice =
        OfficialPluginsTestLocator.LoadIsolatedPlugin(MarketplaceRoot, "voice", "BotAgent.Package.Voice");
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Documents =
        OfficialPluginsTestLocator.LoadIsolatedPlugin(MarketplaceRoot, "documents", "BotAgent.Package.Documents");
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Digest =
        OfficialPluginsTestLocator.LoadIsolatedPlugin(MarketplaceRoot, "digest", "BotAgent.Package.Digest");
    private static readonly (AssemblyLoadContext Context, Assembly Assembly) Knowledge =
        OfficialPluginsTestLocator.LoadIsolatedPlugin(MarketplaceRoot, "knowledge", "BotAgent.Package.Knowledge");

    private static readonly Type StickerCoordinatorType =
        Stickers.Assembly.GetType("BotAgent.Packages.Stickers.StickerCoordinator", throwOnError: true)!;
    private static readonly FieldInfo StickerTimerField =
        StickerCoordinatorType.GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo StickerTickMethod =
        StickerCoordinatorType.GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly Type ProfilesCoordinatorType =
        Profiles.Assembly.GetType("BotAgent.Packages.Profiles.ProfilesCoordinator", throwOnError: true)!;
    private static readonly FieldInfo ProfilesSummaryTimerField =
        ProfilesCoordinatorType.GetField("_summaryTimer", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ProfilesSummaryGateField =
        ProfilesCoordinatorType.GetField("_summaryGate", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static IStickerPolicyContribution StickerPolicy()
        => (IStickerPolicyContribution)Activator.CreateInstance(
            Stickers.Assembly.GetType("BotAgent.Packages.Stickers.StickerPolicyEngine", throwOnError: true)!)!;

    internal static IStickerRuntimeContribution StickerCoordinator(IStickerHostPorts ports)
        => (IStickerRuntimeContribution)Activator.CreateInstance(StickerCoordinatorType, ports)!;

    internal static object? GetStickerTimer(IStickerRuntimeContribution coordinator)
        => StickerTimerField.GetValue(coordinator);

    internal static Task InvokeStickerTickAsync(IStickerRuntimeContribution coordinator)
        => (Task)StickerTickMethod.Invoke(coordinator, null)!;

    internal static IProfilesRuntimeContribution ProfilesCoordinator(IProfilesHostPorts ports)
    {
        var policy = Activator.CreateInstance(
            Profiles.Assembly.GetType("BotAgent.Packages.Profiles.ProfilesPolicyEngine", throwOnError: true)!)!;
        return (IProfilesRuntimeContribution)Activator.CreateInstance(ProfilesCoordinatorType, policy, ports)!;
    }

    internal static object? GetProfilesSummaryTimer(IProfilesRuntimeContribution coordinator)
        => ProfilesSummaryTimerField.GetValue(coordinator);

    internal static SemaphoreSlim GetProfilesSummaryGate(IProfilesRuntimeContribution coordinator)
        => (SemaphoreSlim)ProfilesSummaryGateField.GetValue(coordinator)!;

    internal static IVoiceRuntimeContribution VoiceCoordinator(IVoiceHostPorts ports)
    {
        var policy = Activator.CreateInstance(
            Voice.Assembly.GetType("BotAgent.Packages.Voice.VoicePolicyEngine", throwOnError: true)!)!;
        return (IVoiceRuntimeContribution)Activator.CreateInstance(
            Voice.Assembly.GetType("BotAgent.Packages.Voice.VoiceCoordinator", throwOnError: true)!, policy, ports)!;
    }

    internal static IBotPlugin DocumentsPlugin() => CreatePlugin(Documents.Assembly, "BotAgent.Packages.Documents.DocumentsPackagePlugin");
    internal static IBotPlugin DigestPlugin() => CreatePlugin(Digest.Assembly, "BotAgent.Packages.Digest.DigestPackagePlugin");
    internal static IBotPlugin KnowledgePlugin() => CreatePlugin(Knowledge.Assembly, "BotAgent.Packages.Knowledge.KnowledgePackagePlugin");

    private static IBotPlugin CreatePlugin(Assembly assembly, string typeName)
    {
        try
        {
            return (IBotPlugin)Activator.CreateInstance(assembly.GetType(typeName, throwOnError: true)!)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
