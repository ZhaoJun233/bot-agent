using BotAgent.Domain.Music;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 听过的歌的台账端口（由 <c>Adapters/Persistence/MusicStore</c> 实现，见 §6.4）。
///
/// 为什么要有它：用例层（听歌 / 点歌 / 分享）只该说“这首听过没”“把这次听到记下来”，
/// 不该知道表长什么样、怎么裁剪到上限。有了端口，音乐那几条规则可以**不连库**用假仓储测。
/// </summary>
public interface IMusicRepository
{
    /// <summary>最近听过的若干首（新→旧）。</summary>
    IReadOnlyList<HeardSong> Recent(int count);

    /// <summary>按平台 + 歌曲 id 取一条（没听过 = null）。</summary>
    HeardSong? Get(string key);

    /// <summary>记一次“听到了”（已存在则累加次数并返回库里那份）。</summary>
    HeardSong Remember(HeardSong song);
}
