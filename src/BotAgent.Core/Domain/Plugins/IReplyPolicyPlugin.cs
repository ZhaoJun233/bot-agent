namespace BotAgent.Domain.Plugins;

/// <summary>Only routing facts are exposed to policies; no message bodies or member profiles.</summary>
public readonly record struct ReplyPolicyContext(string SourceKey, bool Proactive, bool DirectMention);

/// <summary>Optional admission policy before context building/model calls. Null allows the reply;
/// a 1–64 character code using lowercase ASCII letters, digits and underscores records silence.
/// Exceptions or malformed codes fail closed as reply_policy_error. Implementations must be thread-safe.</summary>
public interface IReplyPolicyPlugin : IBotPlugin
{
    string? BlockReason(ReplyPolicyContext context);
}
