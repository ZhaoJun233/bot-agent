extern alias StorageModule;

using System.Globalization;
using System.Text.Json;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;
using BotAgent.Services.Memory;
using BotAgent.Services.Official;
using BotAgent.Services.Qq;
using BotAgent.Adapters.Platforms.Feishu;

namespace BotAgent.Host;

internal static partial class CompositionRoot
{
    private static void AssembleUnifiedMemory(Assembly a)
    {
        IUnifiedMemoryPersistence persistence;
        try { persistence = new StorageModule::BotAgent.Storage.UnifiedMemoryStore(); }
        catch { persistence = new UnavailableMemoryPersistence(); }
        a.UnifiedMemory = new BotAgent.Engine.Memory.UnifiedMemory(persistence);
        // This separately configured governance identity never reuses Agent/tool authority.
        var administrator = ReadMemoryAdministrator();
        a.MemoryCommands = new UnifiedMemoryCommandService(a.Source, a.UnifiedMemory,
            context => administrator is not null && administrator.Matches(context));
    }

    private sealed class UnavailableMemoryPersistence : IUnifiedMemoryPersistence
    {
        public UnifiedMemoryState Load() => throw new InvalidOperationException("Unified memory storage unavailable.");
        public void Save(long expectedRevision, UnifiedMemoryState state) => throw new InvalidOperationException("Unified memory storage unavailable.");
    }

    private sealed record MemoryAdministrator(string PlatformId, string AccountScope, string InstanceId, string NativeUserId)
    {
        public bool Matches(UnifiedMemoryContext context) => context.IsReliableIdentity
            && context.Actor is { } actor && context.Driver.PlatformId == PlatformId
            && context.Driver.AccountScope == AccountScope && context.Driver.InstanceId == InstanceId
            && actor.PlatformId == PlatformId && actor.AccountScope == AccountScope && actor.NativeUserId == NativeUserId;
    }

    private static MemoryAdministrator? ReadMemoryAdministrator()
    {
        try
        {
            var json = Environment.GetEnvironmentVariable("BOTAGENT_UNIFIED_MEMORY_ADMIN");
            if (string.IsNullOrWhiteSpace(json)) return null;
            var identity = JsonSerializer.Deserialize<MemoryAdministrator>(json);
            return identity is not null && new[] { identity.PlatformId, identity.AccountScope, identity.NativeUserId }
                .All(ValidMemoryIdentityPart)
                && (identity.InstanceId == string.Empty || ValidMemoryIdentityPart(identity.InstanceId)) ? identity : null;
        }
        catch { return null; } // Missing/invalid configuration never authorizes another identity.
    }

    private static bool ValidMemoryIdentityPart(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256 && value == value.Trim() && !value.Any(char.IsControl);

    private static ParticipantId? MemoryParticipant(PlatformSourceLease source, long alias)
    {
        if (!source.IsCurrent || alias <= 0) return null;
        var driver = source.Origin.Driver;
        string? native = source.Source switch
        {
            OfficialBotGateway official => official.Ids.OriginalOf(alias),
            FeishuBotGateway feishu => feishu.OriginalOf(alias),
            _ when driver.PlatformId == BotAgent.Domain.Platforms.PlatformId.QqPrivate => alias.ToString(CultureInfo.InvariantCulture),
            _ => null
        };
        return string.IsNullOrWhiteSpace(native) ? null : new ParticipantId(driver.PlatformId, driver.AccountScope, native);
    }
}
