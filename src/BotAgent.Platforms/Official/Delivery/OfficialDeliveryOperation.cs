using BotAgent.Domain.Platforms;

namespace BotAgent.Adapters.Official;

public enum OfficialDeliveryState { Prepared, Attempting, Completed, Abandoned, Uploading, Uploaded, CredentialRejected }
public enum OfficialDeliveryPhase { Message, Upload }

/// <summary>A stored operation is a receipt, not permission to invoke HTTP.</summary>
public sealed record OfficialDeliveryOperation(Guid OperationId, int Sequence, OfficialDeliveryState State, DeliveryResult? Result)
{
    public int MessageAttempts { get; init; }
    public int UploadAttempts { get; init; }
    public override string ToString() => $"OfficialDeliveryOperation state={State} sequence={Sequence}";
}

/// <summary>Only a successful BeginAttempt authorizes a new transport invocation.</summary>
public sealed record OfficialDeliveryDecision(bool Allowed, string ReasonCode, OfficialDeliveryOperation? Operation = null);
