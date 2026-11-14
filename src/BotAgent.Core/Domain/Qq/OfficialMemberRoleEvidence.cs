namespace BotAgent.Domain.Qq;

public enum OfficialMemberRole { Unknown, Member, Admin, Owner }

/// <summary>A revocable recent observation for context, never an approval or action authorization.</summary>
public sealed class OfficialMemberRoleEvidence
{
    public OfficialIdentityKey Subject { get; }
    public OfficialMemberRole Role { get; }
    public string Source { get; }
    public DateTimeOffset EventTime { get; }
    public DateTimeOffset ObservedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    private readonly CancellationToken _revoked;

    public OfficialMemberRoleEvidence(OfficialIdentityKey subject, OfficialMemberRole role, string source,
        DateTimeOffset eventTime, DateTimeOffset observedAt, DateTimeOffset expiresAt, CancellationToken revoked)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Scope.Kind != OfficialIdentityKind.GroupMember || !Enum.IsDefined(role)
            || source is not ("GROUP_MESSAGE_CREATE" or "GROUP_AT_MESSAGE_CREATE")
            || expiresAt <= observedAt || expiresAt > observedAt.AddMinutes(5) || !revoked.CanBeCanceled)
            throw new ArgumentException("role_evidence_invalid");
        Subject = subject;
        Role = role;
        Source = source;
        EventTime = eventTime;
        ObservedAt = observedAt;
        ExpiresAt = expiresAt;
        _revoked = revoked;
    }

    public bool IsFresh(DateTimeOffset now)
        => !_revoked.IsCancellationRequested && now >= ObservedAt && now < ExpiresAt;

    public override string ToString() => "OfficialMemberRoleEvidence";
}
