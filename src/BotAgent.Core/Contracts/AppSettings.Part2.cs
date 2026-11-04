using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Domain.Platforms;

namespace BotAgent.Services;

public sealed partial class AppSettings
{
    // ---------- 表情包（全局共用一个库） ----------

    /// <summary>启用表情包：自动收集群友发的图 + 按语境发出去 + 定期自巡检。</summary>
    public bool EnableStickers { get; set; } = true;

    /// <summary>表情包库存储上限（张）。超出按“用得少 + 最久没用”淘汰。</summary>
    public int StickerLibraryMax { get; set; } = 120;

    /// <summary>每次给模型看的候选张数（按语境检索出来的）。</summary>
    public int StickerCandidates { get; set; } = 6;

    /// <summary>表情包自巡检间隔（秒）：机器人自己看一遍库，决定删哪些。0 = 关。</summary>
    public int StickerCurateIntervalSeconds { get; set; } = 3600;

    /// <summary>同一会话两次发表情包的最小间隔（秒）。0 = 不限。</summary>
    public int StickerCooldownSeconds { get; set; } = 120;

    /// <summary>戳一戳：是否处理戳一戳事件（被人戳时按语境回话或戳回去）。</summary>
    public bool EnablePoke { get; set; } = true;

    /// <summary>
    /// 戳一戳冷却（秒）：同一个人连着戳时至少隔这么久才回应一次；
    /// 也是机器人主动戳人的最小间隔。0 = 不限。
    /// </summary>
    public int PokeCooldownSeconds { get; set; } = 45;

    /// <summary>
    /// 模型写的心情保留多久（秒）：超过这个时间没更新就回落到“按被戳次数自动描述”。
    /// 0 = 不过期（一直用模型写的那句）。
    /// </summary>
    public int MoodTtlSeconds { get; set; } = 7200;

    /// <summary>同时向模型发起的最大请求数（按会话串行、跨会话并发）。</summary>
    public int MaxConcurrentReplies { get; set; } = 2;


    // ---------- 语音消息（TTS）----------

    /// <summary>
    /// 启用语音消息：机器人可以用语音说话（模型在 JSON 里填 speak 字段时）。
    /// 默认关 —— 语音比文字“重”，且需要先部署 TTS 服务。
    /// </summary>
    public bool EnableVoice { get; set; }

    /// <summary>
    /// 语音回复积极性 0~100（面板滑杆）。
    /// 它的意思不是“必须发多少条语音”，而是**把分寸交给面板**：
    ///   • 提示词会把这个数告诉模型（如“当前积极性 70/100 → 偏积极”），让它自己校准用得多不多；
    ///   • 同会话的最小间隔也跟着缩放（0 → 2 分钟、50 → 45 秒、100 → 15 秒），
    ///     所以“很积极”真能多说两句，而不是被一道写死的 45 秒门卡住。
    /// </summary>
    public int VoiceEagerness { get; set; } = 50;

    /// <summary>音色（Piper 模型名）。可选：zh_CN-huayan-medium / zh_CN-huayan-x_low / zh_CN-xiao_ya-medium / zh_CN-chaowen-medium。</summary>
    public string VoiceName { get; set; } = "zh_CN-huayan-medium";

    /// <summary>语速（百分比，100 = 原速）。</summary>
    public int VoiceSpeed { get; set; } = 100;

    /// <summary>音调（-12~+12；0 = 不传，用云端默认）。</summary>
    public int VoicePitch { get; set; }

    /// <summary>音量（10~1000 = 0.1~10.0 倍；0 = 不传，用云端默认）。</summary>
    public int VoiceVol { get; set; }

    /// <summary>情绪（happy/sad/angry/surprised/fearful/disgusted/neutral；空 = 不传）。
    /// 只有部分云端音色认这个参数，认不了的会被云端忽略（不会报错）。</summary>
    public string VoiceEmotion { get; set; } = "";

    /// <summary>单条语音的字数上限：超过就不发语音（长了又慢又费流量，不如打字）。</summary>
    public int VoiceMaxChars { get; set; } = 80;

    /// <summary>TTS 服务地址（云端 TTS 旁路容器，提供 /speak?text=… 返回音频）。</summary>
    /// <remarks>
    /// 2026-09-21 起这个地址后面接的是**云端 TTS 代理**（<c>tools/tts-cloud-server.py</c>，
    /// 转发到 MiniMax / OpenAI 兼容接口），不再是本地 Piper。容器名与服务名都没变，
    /// 所以老部署只要把 <c>tts</c> 服务换成新镜像就完事了；契约仍是
    /// <c>GET /speak?text=&amp;voice=&amp;speed=</c> 返 wav（面板试听按 RIFF 校验）。
    /// 注意：**语音名要跟着厂商走**（Piper 的 <c>zh_CN-huayan-medium</c> 云端不认，
    /// 代理会回落成它自己的默认音色；想指定就去面板里选一个云端音色）。
    /// </remarks>
    public string TtsServiceUrl { get; set; } = "http://tts:5000";

    /// <summary>语音合成走哪个云端服务商：<c>minimax</c>（默认）| <c>openai</c>（任何兼容 /v1/audio/speech 的）。</summary>
    public string TtsProvider { get; set; } = "minimax";

    /// <summary>
    /// 云端接口地址（面板可改）。留空 = 用 tts 容器环境变量里的默认值。
    /// 留这个口子是因为同一家厂商有国内站/国际站两个域名（key 与站点要配对），
    /// 不对时值得当场改，而不是去重建容器。
    /// </summary>
    public string TtsApiBase { get; set; } = string.Empty;

    /// <summary>云端模型名（面板可改；留空 = 用容器默认，如 MiniMax 的 speech-2.8-hd）。</summary>
    public string TtsModel { get; set; } = string.Empty;


    // ---------- 链接与分享卡片 ----------

    /// <summary>群里发的链接要不要真打开看一下（取标题/摘要）——给模型“看看里面写了什么”的根据。</summary>
    public bool EnableLinkPreview { get; set; } = true;

    /// <summary>单个链接的抓取超时（秒）。慢站点不能拖住机器人。</summary>
    public int LinkPreviewTimeoutSeconds { get; set; } = 5;

    /// <summary>一条消息里最多预览几个链接。</summary>
    public int LinkPreviewMax { get; set; } = 3;

    // ---------- 联网搜索（模型可以要求“去查一下”） ----------

    /// <summary>启用联网搜索：模型在 JSON 里填 search 字段时，机器人真去搜，拿到事实后再回。默认开。</summary>
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>
    /// 优先用“模型自带搜索”（OpenAI 兼容网关背后的 Gemini 原生端点 + google_search 工具）。
    /// 为什么默认走它：机房 IP 上爬网页搜索基本拿不到结果（Google/Bing/DDG/百度 都拦），
    /// 而模型订阅本来就能搜 —— 不额外要密钥、不爬虫、结果还带来源。
    /// 关掉就只用下面的搜索源模板。
    /// </summary>
    public bool WebSearchUseModelSearch { get; set; } = true;

    /// <summary>
    /// 搜索源模板（每行一条，name|url；{q} = 查询词，会做 URL 编码）。兜底用：
    /// 模型搜索不可用（不是 Gemini、代理不转发工具）时才走这里。
    /// name 决定解析方式：searx* = SearxNG JSON；wiki* = MediaWiki JSON；其余当通用 HTML 抽链接。
    /// 默认给 Wikipedia（实测这台服务器上唯一能直接用的）。
    /// </summary>
    public string WebSearchSources { get; set; } =
        "wiki|https://zh.wikipedia.org/w/api.php?action=query&list=search&srsearch={q}&format=json&srlimit=5&utf8=1";

    /// <summary>每次给模型看几条搜索结果。</summary>
    public int WebSearchMaxResults { get; set; } = 5;

    /// <summary>
    /// 同一个会话两次联网搜索的最小间隔（秒）；0 = 不限。
    /// 为什么要有它：一次搜索 = 一次真实模型调用 + 几秒等待，群里连问几个问题就排队了。
    /// 为什么做成设置项：不同部署的网络快慢、群多少差很多，写死 30 秒众口难调。
    /// </summary>
    public int WebSearchCooldownSeconds { get; set; } = 30;

    /// <summary>搜索/读页面的超时（秒）。</summary>
    public int WebSearchTimeoutSeconds { get; set; } = 20;

    /// <summary>read 页面正文截断长度（字）。</summary>
    public int WebSearchReadMaxChars { get; set; } = 1800;

    // ---------- 听音乐（识别群里的音乐分享 + 网易云歌词 + 波形分析） ----------

    /// <summary>启用“听音乐”：有人分享歌时，自动查歌词、下一份低码率音频分析波形，再交模型接话。</summary>
    public bool EnableMusic { get; set; } = true;

    /// <summary>
    /// 音源模板（分号分隔，按顺序尝试）。占位符：{id} 歌曲 id、{br} 码率、{crc32} 标准 CRC32 的 8 位大写十六进制。
    /// 这类公开音源都是第三方服务，随时会挂、会改参数 —— 所以做成可配置：挂了就在面板里换一条，不用改代码。
    /// </summary>
    public string MusicSources { get; set; } =
        "meting|https://api.qijieya.cn/meting/?type=url&id={id}&br={br};" +
        "gdstudio|https://music-api.gdstudio.xyz/api.php?types=url&source=netease&id={id}&br={br}&s={crc32}";

    /// <summary>下载音频的码率（低码率足够分析波形，也省流量）。</summary>
    public int MusicBitrate { get; set; } = 128;

    /// <summary>
    /// 网易云接口地址（默认官方）。做成可配置是为了能指向自建代理或测试用的假接口；
    /// 服务器在海外时官方接口的音频部分会被地域限制，但歌词/详情没问题。
    /// </summary>
    public string NeteaseBaseUrl { get; set; } = "https://music.163.com";

    /// <summary>单个音频文件体积上限（MB），超过就跳过该音源。</summary>
    public int MusicMaxDownloadMb { get; set; } = 12;

    /// <summary>最多分析多少秒（超出部分截断，控制 CPU）。</summary>
    public int MusicMaxAnalysisSeconds { get; set; } = 180;

    /// <summary>“听过的歌”台账上限（条）。</summary>
    public int MusicLibraryMax { get; set; } = 300;

    /// <summary>分析结果保留天数：超过就重新分析一次（音源/算法可能变过）。</summary>
    public int MusicNoteTtlDays { get; set; } = 30;

    /// <summary>模型想听一首歌时的最小间隔（秒），防同一个话题反复搜歌；0 = 不限。</summary>
    public int MusicListenCooldownSeconds { get; set; } = 120;

    /// <summary>
    /// 专门用来“听”的音频识别模型（**留空 = 不听**，只用手写 DSP 的客观数据）。
    /// 为什么要单独配：很多网关/中转会把音频静默丢掉，主模型根本收不到声音 ——
    /// 这时配一个确实支持音频输入、而且该网关愿意转发的模型，才能“听”出曲风情绪。
    /// 默认留空：具体填哪个模型完全取决于你用的网关，不在代码里替用户指定。
    /// </summary>
    public string MusicUnderstandModel { get; set; } = string.Empty;

    /// <summary>是否把音频片段交给上面的音频识别模型（关掉 = 只用 DSP 实测数据）。</summary>
    public bool MusicSendAudioToModel { get; set; } = true;

    /// <summary>交给模型的音频最多多少 KB（默认 1MB ≈ 60 秒 128kbps）。</summary>
    public int MusicAudioToModelMaxKb { get; set; } = 1024;

    /// <summary>是否把下载的音频留在 data/music/audio（默认不留：服务器上不攒版权内容）。</summary>
    public bool MusicKeepAudio { get; set; }

    /// <summary>
    /// 网易云 Cookie（可选）：带上登录态能少踩一些接口限制。
    /// 属于密钥，环境变量专属（QQCHAT_NETEASE_COOKIE），不写入 settings.json、面板只显示是否已设置。
    /// </summary>
    [JsonIgnore]
    public string NeteaseCookie { get; set; } = string.Empty;

}
