using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Platforms;

/// <summary>Normalized platform capability &amp; exclusive-connection descriptor owned by platform driver packages.</summary>
public sealed record PlatformDriverDescriptor(
    string PlatformId,
    string DisplayName,
    bool ExclusiveConnection,
    bool SupportsText,
    bool SupportsImage,
    bool SupportsVoice,
    bool SupportsQuote);

/// <summary>
/// Generation-bound platform driver descriptor and target admission contract
/// owned by <c>system.platform-*</c> packages. Exclusive-connection drivers MUST use cold adoption
/// and two-phase activation so two connections for the same account never run concurrently.
/// </summary>
public interface IPlatformDriverContribution : IPluginContribution
{
    PlatformDriverDescriptor Descriptor { get; }

    bool RequiresExclusiveConnection { get; }

    bool AllowsTarget(string nativeTargetId, IReadOnlySet<string> allowlist);
}
