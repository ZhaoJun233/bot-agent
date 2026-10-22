using System.Collections.Generic;
using System.Threading.Tasks;
using BotAgent.Domain.Memory;

namespace BotAgent.Domain.Ports;

/// <summary>长期记忆事件片段仓储契约（Domain 端口、零 IO）。</summary>
public interface IEpisodeRepository
{
    Task<long> InsertAsync(EpisodeEntry episode);

    Task<IReadOnlyList<EpisodeEntry>> ListRecentByScopeAsync(string scope, int limit = 10);

    Task<IReadOnlyList<EpisodeEntry>> SearchAsync(string scope, string query, int limit = 5);

    Task DeleteAsync(long id);
}
