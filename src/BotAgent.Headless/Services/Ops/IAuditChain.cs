using BotAgent.Domain.Ops;

namespace BotAgent.Services.Ops;

public sealed record AuditEvent(
    string EventType,
    string ActorId,
    string TenantId,
    string ActionDetail,
    string PolicyVersion);

public sealed record AuditVerification(bool Valid, int? BreakpointId, string? ErrorType, int CheckedCount);

public interface IAuditChain
{
    void Append(AuditEvent auditEvent);
    AuditVerification Verify();
}
