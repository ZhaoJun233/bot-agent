extern alias StorageModule;

namespace BotAgent.Adapters.Persistence;

/// <summary>Compatibility type; all persistence behavior lives in Storage.</summary>
public sealed class MoodStore : StorageModule::BotAgent.Adapters.Persistence.MoodStore
{
    public MoodStore(Func<int>? ttlSeconds = null) : base(ttlSeconds) { }
}