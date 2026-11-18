using BotAgent.Domain.Conversation;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Reply;

public sealed partial class ReplyPipeline
{
    private readonly object _pendingGate = new();
    private sealed class ReplyEpoch { public object Token = new(); }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<BotConversation, ReplyEpoch> _replyEpochs = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (BotConversation Conversation, object Epoch)> _activeConversations = new();
    private object CaptureReplyEpoch(BotConversation conversation) => Volatile.Read(ref _replyEpochs.GetValue(conversation, _ => new ReplyEpoch()).Token);
    private bool IsReplyEpochCurrent(BotConversation conversation, object epoch) => ReferenceEquals(CaptureReplyEpoch(conversation), epoch);
    private void RetireReplyEpoch(BotConversation conversation) => Interlocked.Exchange(ref _replyEpochs.GetValue(conversation, _ => new ReplyEpoch()).Token, new object());

    /// <summary>All reply opportunities use the same pacing, source and stop boundary.</summary>
    public void RequestReply(BotConversation conversation, long? triggerMessageId,
        bool proactive = false, bool directInWindow = false)
        => RequestReplyInEpoch(conversation, triggerMessageId, proactive, directInWindow, CaptureReplyEpoch(conversation));

    private void RequestReplyInEpoch(BotConversation conversation, long? triggerMessageId,
        bool proactive, bool directInWindow, object epoch)
    {
        if (!IsReplyEpochCurrent(conversation, epoch)) return;
        var source = PlatformSourceExecution.Current ?? (_source as IPlatformSourceRouter)?.CaptureForConversation(conversation.SourceKey);
        if (_source is IPlatformSourceRouter && (source is null || !source.IsCurrent)) return;
        using var sourceScope = PlatformSourceExecution.Enter(source);
        RefreshPacingPolicy();
        if (!_settings.AiModeEnabled || !IsReplyEpochCurrent(conversation, epoch)) return;
        ObservePacedReply(conversation, triggerMessageId, proactive, directInWindow, epoch);
    }
}
