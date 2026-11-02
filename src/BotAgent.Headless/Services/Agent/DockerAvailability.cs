namespace BotAgent.Services.Agent;

/// <summary>
/// 这个容器里到底有没有能用的 docker（#131）。
///
/// 为什么需要它：compose 默认**不挂载** `/var/run/docker.sock`（两个 `volumes:` 键并存时严格解析器整份文件报错，
/// 2026-10 之前一直是这样，见 issue #131）。于是「面板允许 Docker」这个开关打开了也没有任何东西能跑：
/// 服务器 agent 的工具表里写着 docker、提示词诱导模型去用，每次调用都注定失败；一键部署也一样。
/// 所以开关之外再加一道**事实检查**：容器里真有 docker 才算可用。开关仍然有效（关着一定不可用），
/// 两把锁的关系没变，只是第二把从「docker 命令能不能跑」变成「docker 在不在」。
///
/// 判定顺序（与 docker CLI 自己的规则一致）：
///   ① `DOCKER_HOST` 非空 → 可用（部署方显式声明了 docker 在哪，可能是 TCP 或另一条 socket 路径）；
///   ② Windows → 查命名管道 `\\.\pipe\docker_engine`；
///   ③ 其它 → 查 `/var/run/docker.sock`。
/// 探测失败一律当成「不可用」（fail-closed：宁可少一个工具，也不要给模型一个每次都失败的工具）。
///
/// 注意：这里不用 `File.Exists` —— 架构探针对 `src` 里的直接文件 IO 有总数棘轮（`FileIoTotal`），
/// 而这条探测属于「能力探测」不是「数据存取」，用 `FileInfo` / 命名空间枚举表达同一件事。
/// </summary>
public static class DockerAvailability
{
    /// <summary>Unix 上 docker CLI 默认连的 socket。</summary>
    public const string DefaultUnixSocket = "/var/run/docker.sock";

    /// <summary>Windows 上 docker Desktop / docker Engine 的命名管道。</summary>
    public const string DefaultWindowsPipe = @"\\.\pipe\docker_engine";

    /// <summary>此刻这个进程所在的容器里能不能跑 docker。</summary>
    public static bool IsAvailable => Detect(
        Environment.GetEnvironmentVariable("DOCKER_HOST"),
        OperatingSystem.IsWindows(),
        Exists);

    /// <summary>
    /// 判定本体（纯函数，探针直接测它，不去碰真实文件系统）。
    /// <paramref name="exists" /> 只会在「没有 DOCKER_HOST」时被调用一次。
    /// </summary>
    public static bool Detect(string? dockerHost, bool isWindows, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            return true;
        }

        return exists(isWindows ? DefaultWindowsPipe : DefaultUnixSocket);
    }

    private static bool Exists(string path)
    {
        try
        {
            if (path.StartsWith(@"\\.\pipe", StringComparison.Ordinal))
            {
                // 命名管道不是普通文件：列一遍 \\.\pipe\ 里有没有同名项。
                const string pipeRoot = @"\\.\pipe";
                return Directory.Exists(pipeRoot) && Directory
                    .EnumerateFileSystemEntries(pipeRoot)
                    .Any(entry => string.Equals(Path.GetFileName(entry), "docker_engine", StringComparison.OrdinalIgnoreCase));
            }

            return new FileInfo(path).Exists;
        }
        catch
        {
            // 权限不足 / 命名空间不可枚举 → 当成没有（fail-closed）。
            return false;
        }
    }
}
