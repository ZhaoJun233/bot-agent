using System.Text.Json;

namespace BotAgent.Domain.Reply;

/// <summary>
/// <see cref="ModelOutputDeclaration" /> 的**媒体与联网**读取（只读不判）：想听/想分享的歌、心情，
/// 语音（说不说 / 说什么 / 什么语气），以及联网查什么、读哪一页。
/// </summary>
public readonly partial record struct ModelOutputDeclaration
{
    /// <summary>想听/想分享的歌 + 心情。</summary>
    private static (string? Listen, string? ShareSong, string? Mood) ReadMedia(JsonElement root)
    {
        // 模型想“听一听”某首歌（歌名/歌手）：群里让它听歌、或它自己想聊某首歌却没把握时用。
        // 机器人会拿这个名字去搜歌，搜到就下低码率音频分析波形，下一轮把歌词 + 实测给它。
        string? listen = null;
        if ((root.TryGetProperty("listen", out var ls) || root.TryGetProperty("听歌", out ls)) && ls.ValueKind == JsonValueKind.String)
        {
            var wanted = ls.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(wanted) && wanted.Length is >= 2 and <= 60)
            {
                listen = wanted;
            }
        }

        // 模型想把某首歌分享给群里（发一张网易云卡片）：“推荐首歌/点歌/聊到某首歌”这类语境
        string? shareSong = null;
        if ((root.TryGetProperty("shareSong", out var ss) || root.TryGetProperty("share_song", out ss)) && ss.ValueKind == JsonValueKind.String)
        {
            var want = ss.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(want) && want.Length is >= 2 and <= 60)
            {
                shareSong = want;
            }
        }

        // 模型顺手写的心情（≤ 24 字）：存起来给下一轮用；太长/非字符串一律忽略
        string? mood = null;
        if (root.TryGetProperty("mood", out var md) && md.ValueKind == JsonValueKind.String)
        {
            mood = md.GetString()?.Trim();
        }

        return (listen, shareSong, mood);
    }

    /// <summary>语音：说不说、说什么、用什么语气。</summary>
    private static (bool Both, string? Speak, string? VoiceEmotion, double? VoiceSpeed, int? VoicePitch) ReadVoice(JsonElement root)
    {
        // 模型声明“语音之外还想让眼睛看到这段文字”（both: true）——2026-09-21 加：
        // 以前是代码猜“文字里有没有语音说不出来的东西”✗（5 位数字算、8 个字母算…纯玄学 ✗），
        // 现在由模型自己表明意图。默认：语音把这轮话说完了 → 文字不再重复发。
        var both = false;
        if (root.TryGetProperty("both", out var bo) || root.TryGetProperty("也发文字", out bo))
        {
            both = bo.ValueKind == JsonValueKind.True;
        }

        // 模型想“用语音说这句”（speak）：值可以是字符串（要说的话），也可以是 true（= 用语音说 reply）。
        // 真正能不能发由上层决定（开关/字数上限/频率门/服务可达），这里只负责取值与基本清洗。
        string? speak = null;
        if (root.TryGetProperty("speak", out var sp) || root.TryGetProperty("用语音说", out sp))
        {
            if (sp.ValueKind == JsonValueKind.String)
            {
                var spoken = sp.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(spoken))
                {
                    speak = spoken;
                }
            }
            else if (sp.ValueKind == JsonValueKind.True)
            {
                // {"speak": true} = 把 reply 用语音说（模型偷懒时也能用）
                var reply = root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString()?.Trim()
                    : null;
                speak = string.IsNullOrWhiteSpace(reply) ? null : reply;
            }
        }

        // 语音的语气参数（可选）：**由模型按这句话的语境自己定**。
        // 省略 → null → 上层用面板里配的默认值（面板不再是唯一来源）。
        // 为什么只认这几个值：云端只认这几个情绪名，乱填/超范围的直接忽略，别让它把合成搞挂。
        string? voiceEmotion = null;
        if (root.TryGetProperty("voiceEmotion", out var vEmo) || root.TryGetProperty("voice_emotion", out vEmo))
        {
            if (vEmo.ValueKind == JsonValueKind.String)
            {
                var e = vEmo.GetString()?.Trim().ToLowerInvariant();
                if (e is "happy" or "sad" or "angry" or "surprised" or "fearful" or "disgusted" or "neutral")
                {
                    voiceEmotion = e;
                }
            }
        }

        double? voiceSpeed = null;
        if ((root.TryGetProperty("voiceSpeed", out var vSpd) || root.TryGetProperty("voice_speed", out vSpd))
            && vSpd.ValueKind == JsonValueKind.Number)
        {
            var v = vSpd.GetDouble();
            if (v is >= 0.5 and <= 2.0)
            {
                voiceSpeed = Math.Round(v, 2);
            }
        }

        int? voicePitch = null;
        if ((root.TryGetProperty("voicePitch", out var vPit) || root.TryGetProperty("voice_pitch", out vPit))
            && vPit.ValueKind == JsonValueKind.Number)
        {
            var v = (int)Math.Round(vPit.GetDouble());
            if (v is >= -12 and <= 12)
            {
                voicePitch = v;
            }
        }

        return (both, speak, voiceEmotion, voiceSpeed, voicePitch);
    }

    /// <summary>联网：查什么 / 读哪页。</summary>
    private static (string? Search, string? Read) ReadNet(JsonElement root)
    {
        // 模型想“上网查一下”（search）/“读一下某个页面”（read）。
        // 真正去查是上层的事（要发 HTTP、有冷却），这里只取词：
        //   • search 太短（<2）/太长（>120）都不要 —— 太长基本是它在写句子，不是搜索词；
        //   • read 必须是 http(s) 地址（SSRF 闸门在上层）。
        string? search = null;
        if ((root.TryGetProperty("search", out var se) || root.TryGetProperty("查一下", out se)) && se.ValueKind == JsonValueKind.String)
        {
            var wanted = se.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(wanted) && wanted.Length is >= 2 and <= 120)
            {
                search = wanted;
            }
        }

        string? read = null;
        if ((root.TryGetProperty("read", out var rd) || root.TryGetProperty("读一下", out rd)) && rd.ValueKind == JsonValueKind.String)
        {
            var url = rd.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(url) && url.Length <= 500 &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                read = url;
            }
        }

        return (search, read);
    }
}
