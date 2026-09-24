using System.IO;
using BotAgent.Domain.Ports;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 听过的歌的音频留档（默认**不留**，只有开了开关才写盘 —— 服务器上不攒版权内容）。
/// 落盘细节集中在这里：key 里带平台前缀（<c>163:12345</c>），文件名要把 ':' 这类字符换掉，
/// 否则 Windows 上直接写不出来。
/// </summary>
public sealed class AudioCache : IAudioCache
{
    /// <summary>把音频写进 <paramref name="dir" />，返回落盘路径。</summary>
    public string Save(string dir, string key, byte[] data)
    {
        Directory.CreateDirectory(dir);
        var safe = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or ':' or '_' ? c : '_').ToArray())
            .Replace(':', '_');
        var path = Path.Combine(dir, safe + ".mp3");
        File.WriteAllBytes(path, data);
        return path;
    }
}
