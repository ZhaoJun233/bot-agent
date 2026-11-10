using System.Collections.Concurrent;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;
using BotAgent.Engine.Runtime;
using BotAgent.Services.Qq;

// Only the host seam is synthetic. The scheduler, coordinator, clock and gate are source-linked unchanged.
namespace BotAgent.Services
{
    public sealed class BotConversation
    {
        private readonly List<ChatMessage> _messages = new();
        public required string SourceKey { get; init; }
        public ConversationKind Kind { get; init; } = ConversationKind.GroupChat;
        public bool HasPendingReply { get; set; }
        public long NextSeq { get { lock (_messages) return _messages.Count; } }
        public IReadOnlyList<ChatMessage> Messages { get { lock (_messages) return _messages.ToArray(); } }
        public (bool, long) Target => (Kind == ConversationKind.GroupChat, 10001);
        public ChatMessage Add(long speaker, bool direct = false)
        {
            lock (_messages)
            {
                var message = new ChatMessage { Role = MessageRole.Peer, Text = "synthetic", SenderId = speaker,
                    Seq = _messages.Count + 1, QqMessageId = _messages.Count + 1, DirectToBot = direct };
                _messages.Add(message);
                return message;
            }
        }
    }
}
namespace BotAgent.Services.Conversations { internal static class ProbeNamespace { } }
namespace BotAgent.Services.Reply
{
    public sealed record ChatDeliveryReport(bool AnyChatSent, bool CompleteChatReply);
    public enum PendingReplyIntent { Ordinary, Explicit, AcceptedResult }
    public sealed partial class ReplyPipeline
    {
        // Legacy queues are excluded from this isolation fixture; real transitions have their own Engine-linked probe.
        private sealed record LegacyPacingTransition;
        private static LegacyPacingTransition RetireLegacyForPacing() => new();
        private static void DispatchLegacyTransition(LegacyPacingTransition transition) { }
        private readonly object _source = new();
        private readonly ProbePermissions _whitelist = new();
        private readonly ProbePolicies _platformPolicies = new();
        private readonly ProbeHooks _hooks = new();
        private readonly ProbeSender _plain = new();
        private readonly object _pendingGate = new();
        private readonly ConcurrentDictionary<string, byte> _inFlight = new();
        private readonly ConcurrentDictionary<string, (BotConversation Conversation, object Epoch)> _activeConversations = new();
        private readonly ConcurrentDictionary<BotConversation, object> _epochs = new();
        private readonly ResizableReplyGate _replyGate = new(1);
        private AppSettings _settings = new() { AiModeEnabled = true, ReplyPacingEnabled = true,
            GroupInterjectionProbability = 100, GroupContinuationWaitSeconds = 3,
            PrivateContinuationWaitSeconds = 1.5, GroupInterjectionCooldownSeconds = 30 };
        public AppSettings Settings => _settings;
        public Func<BotConversation, CancellationToken, IPacedReplyAttempt, Task> Generate { get; set; }
            = (_, _, _) => Task.CompletedTask;
        public int Handoffs;
        public int BusyNotices => _plain.Count;
        public int InFlight => _inFlight.Count;
        public int Works { get { lock (_pacingSync) return _pacedWorks.Count; } }
        public string[] Errors => _hooks.Errors.ToArray();
        public void Ingress(BotConversation conversation, ChatMessage message)
        {
            RefreshPacingPolicy();
            ObservePacedReply(conversation, message.QqMessageId, false, message.DirectToBot, CaptureReplyEpoch(conversation));
        }
        public void Continuation(BotConversation conversation, ChatMessage message) => ObservePacedContinuation(conversation, message);
        public void AcceptedResult(BotConversation conversation) => RequestAcceptedTaskReply(conversation, _settings.Snapshot());
        public void Drop(BotConversation conversation) => DropPacedPending(c => ReferenceEquals(c, conversation));
        public void Stop() => StopPacing();
        public void ReplaceSettings(AppSettings settings) { _settings = settings; RefreshPacingPolicy(); }
        public void Deny() => _whitelist.Allowed = false;
        private object CaptureReplyEpoch(BotConversation conversation) => _epochs.GetOrAdd(conversation, _ => new object());
        private bool IsReplyEpochCurrent(BotConversation conversation, object epoch) => ReferenceEquals(CaptureReplyEpoch(conversation), epoch);
        private void RetireReplyEpoch(BotConversation conversation) => _epochs[conversation] = new object();
        private Task GenerateReplyAsync(BotConversation conversation, long? trigger, bool proactive, CancellationToken ct, IPacedReplyAttempt pacing)
            => Generate(conversation, ct, pacing);
        private void EnqueueReplyInEpoch(BotConversation conversation, long? trigger, bool proactive, bool preserve, object epoch, PendingReplyIntent intent = PendingReplyIntent.Ordinary)
        { if (IsReplyEpochCurrent(conversation, epoch)) Interlocked.Increment(ref Handoffs); }
        private Task DrainReplyQueueAsync() => Task.CompletedTask;
    }
    internal sealed class ProbePermissions
    {
        public bool Allowed = true;
        public bool AllowsKey(string _) => Allowed;
        public bool AllowsSource(PlatformContext _, bool isGroup, long target) => Allowed;
    }
    internal sealed class ProbePolicies
    {
        public ProbePolicy Resolve(PlatformContext _) => new();
        public ProbePolicy ResolveForChannel(string _) => new();
    }
    internal sealed class ProbePolicy
    { public bool Registered => true; public bool Connected => true; public bool Enabled => true; public bool ChatEnabled => true; }
    internal sealed class ProbeHooks
    {
        public readonly ConcurrentQueue<string> Errors = new();
        public bool IsDisposed() => false;
        public void Log(string text) => Errors.Enqueue(text);
    }
    internal sealed class ProbeSender
    {
        public int Count;
        public Task SendPlainAsync(BotConversation _, string text) { Interlocked.Increment(ref Count); return Task.CompletedTask; }
    }
}
