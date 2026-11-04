using System.Collections.Generic;
using System.Threading.Tasks;
using BotAgent.Domain.Jargon;

namespace BotAgent.Domain.Ports;

/// <summary>黑话/俚语持久化端口契约（Domain 端口、零 IO）。</summary>
public interface IJargonRepository
{
    Task<JargonEntry?> GetAsync(string scope, string phrase);

    Task<IReadOnlyList<JargonEntry>> ListByScopeAsync(string scope, JargonStatus? status = null);

    Task<IReadOnlyList<JargonEntry>> ListConfirmedForPromptAsync(string scope, int limit = 20);

    Task UpsertAsync(JargonEntry entry);

    Task SetStatusAsync(long id, JargonStatus status);

    Task DeleteAsync(long id);
}
