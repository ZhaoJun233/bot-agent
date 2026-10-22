namespace BotAgent.Domain.Ports;

/// <summary>
/// <c>//</c> 任务的图片留档端口（由 <c>Adapters/Persistence/AgentImageStore</c> 实现）。
///
/// 为什么要有它：群友发的图要下载下来存一份，服务器上的 agent 才能按路径读到（它看不到图本体，只能读文件）；
/// 过期清理也在实现里。这些是 IO 细节，用例只该说“存一张”“清一下旧的”。
/// </summary>
public interface IAgentImageStore
{
    /// <summary>落一张图，返回文件名（带扩展名）。</summary>
    Task<string> SaveAsync(string dir, string name, byte[] data, CancellationToken ct = default);

    /// <summary>清掉超过 <paramref name="maxAge" /> 的老留档（best-effort：出错不外传，下次再说）。</summary>
    void Prune(string dir, TimeSpan maxAge);
}
