namespace BotAgent.Domain.Ports;

/// <summary>
/// 听过的歌的**音频留档**端口（由 <c>Adapters/Persistence/AudioCache</c> 实现）。
///
/// 与 <see cref="IMusicRepository" /> 分开：那个存“听过什么”（台账），这个存“音频文件本身”（可选、默认不落盘）。
/// 为什么要有它：名字里带平台前缀（<c>163:12345</c>）要换成合法文件名、目录要现建 ——
/// 这些都是落盘细节，用例只该说“把这段音频存下来，给我路径”。
/// </summary>
public interface IAudioCache
{
    /// <summary>把音频写进 <paramref name="dir" />，返回落盘路径。</summary>
    string Save(string dir, string key, byte[] data);
}
