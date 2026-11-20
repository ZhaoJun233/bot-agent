using System.Runtime.CompilerServices;

// Synthetic probes exercise the actual implementation without copying it into host assemblies.
[assembly: InternalsVisibleTo("BotAgent.ProductionSpecProbe")]
[assembly: InternalsVisibleTo("BotAgent.ReviewRemediationProbe")]
[assembly: InternalsVisibleTo("BotAgent.EngineProbe")]
