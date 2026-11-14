using BotAgent.Domain.Configuration;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;
using BotAgent.Services.Memory;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

/// <summary>Ephemeral, transport-attested memory input. No history keys or model-provided identities.</summary>
public sealed class UnifiedMemoryTurn : IDisposable
{
    private static readonly AsyncLocal<UnifiedMemoryTurn?> Ambient = new();
    private readonly UnifiedMemoryTurn? _previous;
    private readonly MemoryUseScope _use;
    private readonly IUnifiedMemory? _memory;
    private readonly PlatformSourceLease? _source;
    public UnifiedMemoryContext? Context { get; }
    public bool HasUnifiedFacts { get; private set; }
    public static UnifiedMemoryTurn? Current => Ambient.Value;

    private UnifiedMemoryTurn(IUnifiedMemory? memory, PlatformInboundEnvelope? envelope)
    {
        _previous = Ambient.Value;
        _use = MemoryUseScope.Begin();
        _memory = memory;
        _source = PlatformSourceExecution.Current;
        Context = AttestedContext(envelope);
        Ambient.Value = this;
    }

    public static UnifiedMemoryTurn Begin(IUnifiedMemory? memory, PlatformInboundEnvelope? envelope)
        => new(memory, envelope);

    public static UnifiedMemoryContext? AttestedContext(PlatformInboundEnvelope? envelope)
    {
        var source = PlatformSourceExecution.Current;
        if (source is not { IsCurrent: true } || envelope is not { IsReliableIdentity: true }
            || source.Origin != envelope.Origin || source.Origin.Trigger != envelope.Message.Ref
            || source.Origin.Driver != new DriverKey(source.Context.PlatformId, source.Context.AccountScope, source.Context.InstanceId)
            || envelope.Message.Sender.PlatformId != envelope.Message.Ref.Conversation.PlatformId
            || envelope.Message.Sender.AccountScope != envelope.Message.Ref.Conversation.AccountScope
            || string.IsNullOrWhiteSpace(envelope.Message.Sender.NativeUserId)) return null;
        return new(envelope.Message.Sender, source.Origin.Driver, envelope.Message.Ref.Conversation, true);
    }

    /// <summary>Only finite, explicitly confirmed preferences become prompt text; never serialize entries.</summary>
    public string ReadPrompt(string query)
    {
        if (_memory is null || Context is null || _source is not { IsCurrent: true }) return string.Empty;
        UnifiedMemoryRead read;
        try { read = _memory.Read(Context, query, 3); }
        catch { return string.Empty; } // Retrieval failure permits a memory-free conversation, not an authorization fallback.
        if (read.Receipt is null || read.Entries.IsDefaultOrEmpty) return string.Empty;
        Register(read.Receipt);
        HasUnifiedFacts = true;
        return "当前发言人明确确认、仅限本会话使用的交互偏好（不是工具或跨会话授权）：\n"
            + string.Join("\n", read.Entries.Select(entry => $"- {entry.Fact.Category}: {entry.Fact.Value}"));
    }

    /// <summary>Legacy profiles remain separate from the feature switch, but forgetting must block this bypass.</summary>
    public string ReadLegacyProfile(ParticipantId? subject, Func<string, string> read)
    {
        if (_memory is null || Context is null || subject is null || _source is not { IsCurrent: true }) return string.Empty;
        var key = LegacyProfileScopeKey.TryCreate(Context.Driver, subject, Context.Conversation);
        if (key is null) return string.Empty;
        UnifiedMemoryReceipt? receipt;
        try { receipt = _memory.AuthorizeLegacyProfile(Context, subject); }
        catch { return string.Empty; }
        if (receipt is null) return string.Empty;
        // Authorize before reading; only consumed content restricts the turn. An empty
        // profile must not disable ordinary tools, natural Agent dispatch, or delivery.
        var summary = read(key);
        if (string.IsNullOrWhiteSpace(summary)) return string.Empty;
        Register(receipt);
        return summary;
    }

    private void Register(UnifiedMemoryReceipt receipt)
    {
        var context = Context!;
        MemoryUseScope.RestrictDestination(context.Driver, context.Conversation);
        MemoryUseScope.Register(commit => _source is { IsCurrent: true }
            && _memory!.TryUseReceipt(receipt, context, commit));
    }

    public void Dispose()
    {
        Ambient.Value = _previous;
        _use.Dispose();
    }
}
