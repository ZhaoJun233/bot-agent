using System.IO;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// <c>//</c> 任务的图片留档：群友发的图下载下来存一份，服务器上的 agent 才能按路径读到
/// （它看不到图本体，只能读文件）。过期清理也在这里 —— IO 细节不该长在用例里。
/// </summary>
public sealed class AgentImageStore : IAgentImageStore
{
    /// <summary>落一张图，返回文件名（带扩展名）。</summary>
    public async Task<string> SaveAsync(string dir, string name, byte[] data, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, name), data, ct);
        return name;
    }

    /// <summary>清掉超过 <paramref name="maxAge" /> 的老留档（best-effort：出错不外传，下次再说）。</summary>
    public void Prune(string dir, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (Clock.UtcNow - new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) > maxAge)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败无所谓，下次再说
        }
    }
}
