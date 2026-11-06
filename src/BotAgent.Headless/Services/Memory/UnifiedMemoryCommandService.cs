using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Ports;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Memory;

/// <summary>
/// Explicit self-service commands, consumed before history/model dispatch. The administrator
/// predicate governs only the global switch; it never changes the subject of an operation.
/// </summary>
public sealed class UnifiedMemoryCommandService
{
    private readonly IQqChatSource _source;
    private readonly IUnifiedMemory _memory;
    private readonly Func<UnifiedMemoryContext, bool> _isAdministrator;

    public UnifiedMemoryCommandService(IQqChatSource source, IUnifiedMemory memory,
        Func<UnifiedMemoryContext, bool> isAdministrator)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _isAdministrator = isAdministrator ?? throw new ArgumentNullException(nameof(isAdministrator));
    }

    /// <summary>Synchronously consume the reserved prefix, including invalid/unattested attempts.</summary>
    public bool TryHandle(QqChatMessage message)
    {
        if (!Recognized(message)) return false;
        _ = HandleAsync(message); // HandleAsync owns all failures; no command falls through to a model.
        return true;
    }

    public async Task<bool> HandleAsync(QqChatMessage message)
    {
        if (!Recognized(message)) return false;
        try
        {
            using var memoryScope = MemoryUseScope.Begin();
            var lease = PlatformSourceExecution.Current;
            var context = TryContext(message);
            if (context is null || lease is null || !ReferenceEquals(lease.Owner, _source)
                || _source is not IPlatformSourceRouter router) return true;
            string reply;
            try { reply = Execute(context, message.Envelope!.Message.Text); }
            catch { reply = context.Conversation.Kind == ConversationKind.PrivateChat ? MemoryCommandView.Unavailable : MemoryCommandView.GroupOnly; }
            if (!lease.IsCurrent || PlatformSourceExecution.Current != lease) return true;
            // Compatibility aliases belong exclusively to the current router; memory retains the
            // full attested native identity, never the scalar fields of QqChatMessage.
            var target = router.CompatibilityTarget(ConversationIdCodec.Encode(context.Conversation));
            await _source.SendTextAsync(target.IsGroup, target.Id, reply).ConfigureAwait(false);
        }
        catch
        {
            // User command/transport failures intentionally disclose no IDs, tokens, content or exceptions.
        }
        return true;
    }

    /// <summary>Only the actual envelope plus its still-current driver lease can attest identity.</summary>
    public static UnifiedMemoryContext? TryContext(QqChatMessage message)
    {
        var envelope = message.Envelope;
        var lease = PlatformSourceExecution.Current;
        if (envelope is null || lease is not { IsCurrent: true, Origin: not null }
            || lease.Origin != envelope.Origin || envelope.Origin.Trigger != envelope.Message.Ref) return null;
        var origin = envelope.Origin;
        var conversation = envelope.Message.Ref.Conversation;
        var actor = envelope.Message.Sender;
        var driver = new DriverKey(lease.Context.PlatformId, lease.Context.AccountScope, lease.Context.InstanceId);
        if (driver != origin.Driver || !MemoryCommandSyntax.Atom(actor.NativeUserId)
            || !MemoryCommandSyntax.Atom(driver.PlatformId) || !MemoryCommandSyntax.Atom(driver.AccountScope)
            || !MemoryCommandSyntax.Instance(driver.InstanceId) || !MemoryCommandSyntax.Atom(conversation.NativeTargetId)
            || (conversation.ThreadId is not null && !MemoryCommandSyntax.Atom(conversation.ThreadId))
            || conversation.Kind is not (ConversationKind.PrivateChat or ConversationKind.GroupChat or ConversationKind.ChannelChat)
            || conversation.PlatformId != driver.PlatformId || conversation.AccountScope != driver.AccountScope
            || actor.PlatformId != driver.PlatformId || actor.AccountScope != driver.AccountScope) return null;
        return new(actor, driver, conversation, IsReliableIdentity: true);
    }

    private static bool Recognized(QqChatMessage message) => MemoryCommandSyntax.IsCommand(message.Text)
        || MemoryCommandSyntax.IsCommand(message.Envelope?.Message.Text);

    private string Execute(UnifiedMemoryContext context, string text)
    {
        var words = MemoryCommandSyntax.Words(text);
        var validPrefix = words.Length > 0 && words[0].Equals(MemoryCommandSyntax.Prefix, StringComparison.OrdinalIgnoreCase);
        var command = validPrefix && words.Length > 1 ? words[1] : "";
        if (context.Conversation.Kind != ConversationKind.PrivateChat)
        {
            if (command == "remember" && MemoryCommandSyntax.TryRequest(words, 2, context, out var groupRequest))
                _memory.RequestRemember(context, groupRequest);
            return MemoryCommandView.GroupOnly;
        }
        if (!validPrefix) return MemoryCommandView.Unavailable;
        if (words.Length == 1 || command == "help" && words.Length == 2) return MemoryCommandView.Help;
        if (command == "scope" && words.Length == 2) return MemoryCommandSyntax.ScopeToken(new(context.Driver, context.Conversation));
        if (command is "start" or "stop" && words.Length == 2)
            return _isAdministrator(context) ? Result(_memory.SetEnabled(command == "start")) : MemoryCommandView.Unavailable;
        if (command == "remember" && MemoryCommandSyntax.TryRequest(words, 2, context, out var request))
            return Result(_memory.RequestRemember(context, request), review: true);
        if (command == "list") return List(context, words);
        if (words.Length < 3 || !MemoryCommandSyntax.TryId(words[2], out var factId)) return MemoryCommandView.Unavailable;
        return command switch
        {
            "show" when words.Length == 3 => Show(context, factId),
            "confirm" => Confirm(context, factId, words),
            "edit" => Edit(context, factId, words),
            "revoke" when words.Length == 4 && MemoryCommandSyntax.TryScope(words[3], context, out var target)
                => Result(_memory.Revoke(context, factId, target)),
            "reject" when words.Length == 3 => Result(_memory.Reject(context, factId)),
            "forget" when words.Length == 3 => Result(_memory.Forget(context, factId)),
            _ => MemoryCommandView.Unavailable
        };
    }

    private string List(UnifiedMemoryContext context, string[] words)
    {
        var page = 1;
        if (words.Length > 3 || words.Length == 3 && !MemoryCommandSyntax.TryVersion(words[2], out page))
            return MemoryCommandView.Unavailable;
        return MemoryCommandView.List(_memory.List(context), page);
    }

    private string Show(UnifiedMemoryContext context, Guid factId)
    {
        var entry = _memory.List(context).FirstOrDefault(e => e.FactId == factId);
        return entry is null ? MemoryCommandView.Unavailable : MemoryCommandView.Show(entry);
    }

    private string Confirm(UnifiedMemoryContext context, Guid factId, string[] words)
    {
        if (words.Length != 7 || !MemoryCommandSyntax.TryVersion(words[3], out var version)
            || !MemoryCommandSyntax.TryId(words[4], out var token) || words[5] != "allow-source"
            || words[6] is not ("accept-group-audience" or "private-only")) return MemoryCommandView.Unavailable;
        return Result(_memory.Confirm(context, new(factId, version, token, AllowSourceLocal: true,
            AcknowledgeGroupAudience: words[6] == "accept-group-audience")));
    }

    private string Edit(UnifiedMemoryContext context, Guid factId, string[] words)
    {
        if (words.Length < 6 || !MemoryCommandSyntax.TryVersion(words[3], out var version)
            || !MemoryCommandSyntax.TryRequest(words, 4, context, out var replacement)) return MemoryCommandView.Unavailable;
        return Result(_memory.Edit(context, factId, version, replacement), review: true);
    }

    private static string Result(UnifiedMemoryResult result, bool review = false) => result.Status != UnifiedMemoryStatus.Success
        ? MemoryCommandView.Unavailable
        : review && result.Entry is { } entry ? MemoryCommandView.Show(entry) : MemoryCommandView.Done;
}
