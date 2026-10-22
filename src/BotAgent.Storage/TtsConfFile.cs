using System.IO;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 面板改云端 TTS 配置后写给旁路容器的 <c>tts.env</c>（那个容器启动时读它）。
/// 两个候选目录：宿主机挂载点 → 运行目录兜底；第一个写得成就返回 true。
/// 为什么在持久化层：写文件的细节（目录、权限 600、谁挂没挂）不该长在面板 handler 里。
/// </summary>
public static class TtsConfFile
{
    public static bool TryWrite(string body)
    {
        foreach (var dir in new[] { "/host/qqchat/tts-conf", Path.Combine(AppPaths.RuntimeRoot, "tts-conf") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "tts.env");
                File.WriteAllText(path, body);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 600：里面是密钥
                }

                return true;
            }
            catch
            {
                // 这个目录写不了就试下一个（宿主机目录可能没挂/只读）
            }
        }

        return false;
    }
}
