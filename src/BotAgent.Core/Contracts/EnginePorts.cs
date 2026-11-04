using BotAgent.Domain.Platforms;

namespace BotAgent.Services
{
    /// <summary>Read-only view of the current published configuration snapshot.</summary>
    public interface ISettingsBox
    {
        AppSettings Current { get; }
    }
}

namespace BotAgent.Services.Platforms
{
    /// <summary>Effective platform policy lookup used by Engine use cases.</summary>
    public interface IPlatformPolicyResolver
    {
        EffectivePlatformPolicy Resolve(PlatformContext context);
        EffectivePlatformPolicy ResolveForChannel(string? channel);
        bool IsChatEnabled(string? channel);
    }
}

namespace BotAgent.Services.Resilience
{
    /// <summary>Protocol-risk backoff view used by the reply path.</summary>
    public interface IProtocolRiskBackoff
    {
        bool IsActive(string sourceKey);
        bool HasAnyActive();
        ProtocolBackoffDecision EvaluateText(string sourceKey, bool directAddress, string text);
    }
}
