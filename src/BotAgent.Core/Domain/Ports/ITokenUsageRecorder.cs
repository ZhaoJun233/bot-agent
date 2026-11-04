namespace BotAgent.Domain.Ports;

/// <summary>Records completed chat-model usage independently of optional budget enforcement.</summary>
public interface ITokenUsageRecorder
{
    void RecordUsage(string tenantId, int promptTokens, int completionTokens);
}
