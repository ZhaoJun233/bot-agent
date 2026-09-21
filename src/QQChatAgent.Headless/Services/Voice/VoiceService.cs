using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.Services.Voice;

/// <summary>
/// 语音（TTS）服务客户端 —— 对接 Piper 旁路容器（仓库里的 <c>tools/tts-server.py</c>）：
///   <c>GET /speak?text=…&amp;voice=…&amp;speed=…</c> → 返回 wav
///   <c>GET /health</c>                            → 列出可用音色
///
/// 这里有个关键设计：**机器人不搬运音频**。发语音时只把 <c>/speak</c> 的 URL 交给协议端
/// （见 <c>OneBotGateway.SendVoiceAsync</c>），由 NapCat 自己去下载、转 silk、上传。
/// 好处是机器人这边完全不用碰 silk 编码与 base64，也不用把音频塞进 WebSocket；
/// 代价是协议端必须能访问到这个地址（容器同网段时就是 <c>http://tts:5000</c>）。
///
/// 只有面板「试听一句」才真把 wav 拉到本地 —— 那是给浏览器播的。
/// </summary>
public sealed class VoiceService
{
    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string> _log;

    public VoiceService(HttpClient http, Func<AppSettings> settings, Action<string> log)
    {
        _http = http;
        _settings = settings;
        _log = log;
    }

    /// <summary>当前配置的 TTS 服务根地址（去掉尾部斜杠）；没配/不是 http(s) 绝对地址时返回 null。</summary>
    public string? BaseUrl => Normalize(_settings().TtsServiceUrl);

    /// <summary>当前音色（Piper 模型名），空则回落到默认。</summary>
    public string VoiceName
    {
        get
        {
            var name = _settings().VoiceName?.Trim();
            return string.IsNullOrWhiteSpace(name) ? "zh_CN-huayan-medium" : name;
        }
    }

    /// <summary>语速倍数（设置里是百分比，100 = 原速）。</summary>
    public double Speed => Math.Clamp(_settings().VoiceSpeed, 50, 200) / 100.0;

    /// <summary>
    /// 拼出发给协议端的 <c>/speak</c> 绝对地址。text 为空、服务地址不合法时返回 null
    /// （上层据此降级成发文字，而不是把一条空语音发出去）。
    /// </summary>
    public string? BuildSpeakUrl(string text, string? voiceOverride = null, int? speedOverride = null)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var baseUrl = BaseUrl;
        if (baseUrl is null)
        {
            return null;
        }

        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? VoiceName : voiceOverride!.Trim();
        var speedPercent = Math.Clamp(speedOverride ?? _settings().VoiceSpeed, 50, 200);
        var speed = speedPercent / 100.0;

        // 只对参数做转义：文本里可能有 & # 空格和中文，不转义会把查询串撕碎
        return $"{baseUrl}/speak?text={Uri.EscapeDataString(trimmed)}" +
               $"&voice={Uri.EscapeDataString(voice)}" +
               $"&speed={speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 真把一段文本合成成 wav 字节（面板「试听一句」用）。
    /// 失败时返回 (null, 原因)，原因直接来自 TTS 服务的 JSON error 字段（如"音色不存在"）。
    /// </summary>
    public async Task<(byte[]? Data, string? Error)> SynthesizeAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        var url = BuildSpeakUrl(text, voiceOverride, speedOverride);
        if (url is null)
        {
            return (null, string.IsNullOrWhiteSpace(text) ? "没有要合成的文本" : "TTS 服务地址没配置（应形如 http://tts:5000）");
        }

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 服务端失败时返回的是 JSON（{"error": "…"}），把这句话原样带给面板
                var detail = System.Text.Encoding.UTF8.GetString(bytes);
                try
                {
                    detail = JsonNode.Parse(detail)?["error"]?.GetValue<string>() ?? detail;
                }
                catch
                {
                    // 不是 JSON 就用原文（截断，别把整页 HTML 塞进面板）
                }

                if (detail.Length > 300)
                {
                    detail = detail[..300];
                }

                _log($"[Voice] TTS 合成失败 HTTP {(int)resp.StatusCode}：{detail}");
                return (null, $"HTTP {(int)resp.StatusCode}：{detail}");
            }

            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                // 不是 wav：多数是配置错（地址指到了一个网页），说出来比"静默无声"强
                return (null, $"TTS 返回的不是 wav（{bytes.Length} 字节，HTTP {resp.Content.Headers.ContentType}）");
            }

            return (bytes, null);
        }
        catch (Exception ex)
        {
            _log($"[Voice] TTS 请求异常：{ex.Message}");
            return (null, ex.Message);
        }
    }

    /// <summary>问一下 TTS 服务自己：活着吗、有哪些音色（面板用来校验地址与音色名）。</summary>
    // ---------- 音色复刻（MiniMax Voice Clone） ----------
    //
    // 为什么做进面板：复刻的产物就是一个 voice_id 字符串，填到「音色」那格就能用 ——
    // 但复刻本身要先上传一段音频（10 秒 ~ 5 分钟）再调克隆接口，开 SSH 跑 curl 太别扭。
    // 两步都是一次性管理动作，所以直接在这边调云端，不走 tts 容器。
    // 官方约束：克隆音色 **7 天没用就会被系统删掉**；同一个 voice_id 重复克隆报 2039。

    /// <summary>复刻用的云端根地址；服务商是 openai 兼容网关时没有这套接口 → null。</summary>
    private string? CloneBaseUrl()
    {
        if (string.Equals(_settings().TtsProvider?.Trim(), "openai", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fromPanel = _settings().TtsApiBase?.Trim();
        return string.IsNullOrWhiteSpace(fromPanel) ? "https://api.minimaxi.com" : fromPanel.TrimEnd('/');
    }

    private string? CloneKey()
    {
        var key = SecretsStore.LoadTtsKey();
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    /// <summary>列出已经复刻过的音色（<c>/v1/get_voice</c> 的 voice_cloning 那一类）。</summary>
    public async Task<(List<string> Voices, string? Error)> ListClonedVoicesAsync(CancellationToken ct)
    {
        var list = new List<string>();
        var baseUrl = CloneBaseUrl();
        var key = CloneKey();
        if (baseUrl is null)
        {
            return (list, "当前服务商不支持音色复刻（只有 MiniMax 这套接口有）");
        }

        if (key is null)
        {
            return (list, "还没配云端 TTS 密钥 —— 先在「云端 TTS 密钥」那格填一个，复刻用同一个 key");
        }

        try
        {
            var body = new JsonObject { ["voice_type"] = "voice_cloning" };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/get_voice")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (list, $"云端返回 HTTP {(int)resp.StatusCode}：{Shorten(text)}");
            }

            var json = JsonNode.Parse(text);
            var status = NodeInt(json?["base_resp"]?["status_code"]);
            if (status != 0)
            {
                return (list, $"云端报错 {status}：{NodeText(json?["base_resp"]?["status_msg"])}");
            }

            foreach (var node in json?["voice_cloning"]?.AsArray() ?? new JsonArray())
            {
                var id = NodeText(node?["voice_id"]);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(id);
                }
            }

            return (list, null);
        }
        catch (Exception ex)
        {
            return (list, "列复刻音色失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 复刻一个音色：先上传音频拿 file_id，再用自定义 voice_id 建克隆。
    /// 音频要求（官方）：mp3/m4a/wav，10 秒 ~ 5 分钟，≤ 20MB。
    /// </summary>
    public async Task<(bool Ok, string? Error)> CloneVoiceAsync(byte[] audio, string fileName, string voiceId, CancellationToken ct)
    {
        var baseUrl = CloneBaseUrl();
        var key = CloneKey();
        if (baseUrl is null)
        {
            return (false, "当前服务商不支持音色复刻（只有 MiniMax 这套接口有）");
        }

        if (key is null)
        {
            return (false, "还没配云端 TTS 密钥 —— 复刻用同一个 key，先在「云端 TTS 密钥」那格填一个");
        }

        var wanted = (voiceId ?? string.Empty).Trim();
        if (wanted.Length < 3 || wanted.Length > 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(wanted, "^[A-Za-z0-9_-]+$"))
        {
            return (false, "自定义音色 ID 只能用小写字母/数字/下划线/连字符（3~64 位），例如 zhao_voice_01");
        }

        if (audio.Length == 0)
        {
            return (false, "没有拿到音频内容");
        }

        if (audio.Length > 20 * 1024 * 1024)
        {
            return (false, $"音频 {audio.Length / 1024 / 1024}MB 超过官方上限 20MB（一般 30 秒 ~ 1 分钟就够）");
        }

        try
        {
            // ① 上传样本
            using var upload = new MultipartFormDataContent();
            var filePart = new ByteArrayContent(audio);
            filePart.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            upload.Add(filePart, "file", string.IsNullOrWhiteSpace(fileName) ? "sample.mp3" : fileName);
            upload.Add(new StringContent("voice_clone"), "purpose");

            using var uploadReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/files/upload") { Content = upload };
            uploadReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var uploadResp = await _http.SendAsync(uploadReq, ct);
            var uploadText = await uploadResp.Content.ReadAsStringAsync(ct);
            var uploadJson = JsonNode.Parse(uploadText);
            var fileId = NodeText(uploadJson?["file"]?["file_id"]);
            if (!uploadResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(fileId))
            {
                return (false, $"上传样本失败（HTTP {(int)uploadResp.StatusCode}）：{Shorten(uploadText)}");
            }

            // ② 建克隆
            var cloneBody = new JsonObject { ["file_id"] = fileId, ["voice_id"] = wanted };
            using var cloneReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/voice_clone")
            {
                Content = new StringContent(cloneBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            cloneReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var cloneResp = await _http.SendAsync(cloneReq, ct);
            var cloneText = await cloneResp.Content.ReadAsStringAsync(ct);
            var cloneJson = JsonNode.Parse(cloneText);
            var status = NodeInt(cloneJson?["base_resp"]?["status_code"]);
            if (!cloneResp.IsSuccessStatusCode || status != 0)
            {
                var msg = NodeText(cloneJson?["base_resp"]?["status_msg"]);
                var why = status switch
                {
                    2013 => "（2013 = 参数/样本不合格：需 mp3/m4a/wav、**10 秒 ~ 5 分钟**、≤ 20MB、单人干净人声；voice_id 只能用字母/数字/下划线/连字符）",
                    2039 => "（2039 = 这个 voice_id 已经克隆过了，换个 ID 或先删旧的）",
                    1004 or 2049 => "（key 与站点不配对：国内站 api.minimaxi.com / 国际站 api.minimax.io，面板「云端接口地址」要跟 key 对应）",
                    2042 => "（账号没有音色复刻权限/额度）",
                    _ => string.Empty
                };
                return (false, $"克隆失败（HTTP {(int)cloneResp.StatusCode}，status {status}）：{(msg.Length > 0 ? msg : Shorten(cloneText))}{(why.Length > 0 ? " " + why : string.Empty)}");
            }

            _log($"[Voice] 音色复刻成功：{wanted}（样本 {audio.Length / 1024}KB）");
            return (true, null);        }
        catch (Exception ex)
        {
            return (false, "复刻失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 宽容地取一段文本：官方响应里同一个字段时而是字符串时而是数字
    /// （例如上传返回的 <c>file_id</c> 实测是**数字**）。硬用 <c>GetValue&lt;string&gt;()</c> 会直接抛
    /// “An element of type 'Number' cannot be converted to a 'System.String'”——这个仓库已经踩过一次，别再踩。
    /// </summary>
    private static string NodeText(JsonNode? node)
    {
        if (node is null)
        {
            return string.Empty;
        }

        try
        {
            if (node is JsonValue value)
            {
                if (value.TryGetValue<string>(out var text))
                {
                    return text ?? string.Empty;
                }

                if (value.TryGetValue<long>(out var number))
                {
                    return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (value.TryGetValue<double>(out var dbl))
                {
                    return dbl.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                if (value.TryGetValue<bool>(out var flag))
                {
                    return flag ? "true" : "false";
                }
            }
        }
        catch (Exception)
        {
            // 取不出来就当没有
        }

        return string.Empty;
    }

    private static int NodeInt(JsonNode? node, int fallback = 0)
    {
        var text = NodeText(node);
        return int.TryParse(text, out var parsed) ? parsed : fallback;
    }

    // ---------- 把多段短样本拼成一段（面板「5 秒切片」那种情况） ----------
    //
    // 为什么需要：官方要求主样本 **≥ 10 秒**，而号主手上的切片常常只有 5 秒左右 ✗。
    // 与其让人先去装 ffmpeg，不如在服务端拼：
    //   • WAV（PCM）：解析 fmt/data 两个块、首尾相接、重写头部 —— 无损，最稳；
    //   • MP3：剥掉 ID3v2 头与 ID3v1 尾，再把音频帧拼起来 —— 同采样率/声道时解码器能正常接；
    //   • m4a/aac、或混格式：这里**明确拒绝**（要做得好得把 ffmpeg 塞进镜像，为一次性动作不值）。
    // 拼接处各加 300ms 静音：ASR/声学模型对“硬切”很敏感，留口气识别率明显好。

    /// <summary>拼多段样本成一段。samples 顺序即拼接顺序。</summary>
    public static (byte[]? Data, string? Error, string Note) ConcatSamples(IReadOnlyList<(byte[] Data, string Name)> samples)
    {
        if (samples.Count == 0)
        {
            return (null, "没有样本", string.Empty);
        }

        if (samples.Count == 1)
        {
            return (samples[0].Data, null, string.Empty);
        }

        var allWav = samples.All(s => IsWav(s.Data));
        var allMp3 = samples.All(s => IsMp3(s.Data));
        if (allWav)
        {
            return ConcatWav(samples);
        }

        if (allMp3)
        {
            return ConcatMp3(samples);
        }

        if (samples.Any(s => IsM4a(s.Data)) || samples.Any(s => !IsWav(s.Data) && !IsMp3(s.Data)))
        {
            return (null, "这些切片里有 mm4a/其它格式 —— 面板只能拼 **mp3 与 wav**（都是 mp3 或都是 wav）；" +
                          "m4a 请先在本地导出成 mp3 再传（手机录音默认就是 m4a，很多播放器能直接转）", string.Empty);
        }

        return (null, "样本格式不一致：一部分是 wav、一部分是 mp3 —— 全部统一成同一种再传", string.Empty);
    }

    private static bool IsWav(byte[] d) => d.Length > 44 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F'
                                           && d[8] == 'W' && d[9] == 'A' && d[10] == 'V' && d[11] == 'E';

    private static bool IsMp3(byte[] d) => d.Length > 4
                                           && ((d[0] == 'I' && d[1] == 'D' && d[2] == '3') || (d[0] == 0xFF && (d[1] & 0xE0) == 0xE0));

    private static bool IsM4a(byte[] d) => d.Length > 12 && d[4] == 'f' && d[5] == 't' && d[6] == 'y' && d[7] == 'p';

    private static (byte[]? Data, string? Error, string Note) ConcatWav(IReadOnlyList<(byte[] Data, string Name)> samples)
    {
        ushort channels = 0;
        uint rate = 0;
        ushort bits = 0;
        var pcm = new List<byte[]>();

        foreach (var (data, name) in samples)
        {
            var (ok, ch, rt, bt, chunk) = ReadWav(data);
            if (!ok)
            {
                return (null, $"读不了这段 wav（{name}）—— 可能是非标准头部（手机导出/剪辑软件有时会写额外块）", string.Empty);
            }

            if (channels == 0)
            {
                (channels, rate, bits) = (ch, rt, bt);
            }
            else if (ch != channels || rt != rate || bt != bits)
            {
                return (null, $"这些 wav 的参数不一致（{name} 是 {rt}Hz/{ch}声道/{bt}bit，前面的是 {rate}Hz/{channels}声道/{bits}bit）—— " +
                              "请先统一成同一采样率再拼", string.Empty);
            }

            pcm.Add(chunk);
        }

        // 拼：每段之间插 300ms 静音（16bit 采样：一帧 = channels * bits/8 字节）
        var bytesPerFrame = Math.Max(1, channels * (bits / 8));
        var gap = new byte[bytesPerFrame * (int)(rate * 0.3)];
        var total = pcm.Sum(p => p.Length) + gap.Length * (pcm.Count - 1);
        var outBuf = new byte[44 + total];
        WriteWavHeader(outBuf, channels, rate, bits, total);
        var pos = 44;
        for (var i = 0; i < pcm.Count; i++)
        {
            if (i > 0)
            {
                Array.Copy(gap, 0, outBuf, pos, gap.Length);
                pos += gap.Length;
            }

            Array.Copy(pcm[i], 0, outBuf, pos, pcm[i].Length);
            pos += pcm[i].Length;
        }

        var seconds = total / (double)bytesPerFrame / rate;
        return (outBuf, null, $"把 {samples.Count} 段 wav 拼成了一段（共 {seconds:F1} 秒，段间 300ms 静音）");
    }

    /// <summary>读一个 wav：只找 fmt 与 data 两个块（RIFF 里其它块一律跳过）。</summary>
    private static (bool Ok, ushort Channels, uint Rate, ushort Bits, byte[] Pcm) ReadWav(byte[] d)
    {
        if (!IsWav(d))
        {
            return (false, 0, 0, 0, Array.Empty<byte>());
        }

        ushort channels = 0;
        uint rate = 0;
        ushort bits = 0;
        var pos = 12;
        while (pos + 8 <= d.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(d, pos, 4);
            var size = BitConverter.ToInt32(d, pos + 4);
            var body = pos + 8;
            if (size < 0 || body + size > d.Length)
            {
                break;
            }

            if (id == "fmt ")
            {
                channels = BitConverter.ToUInt16(d, body + 2);
                rate = BitConverter.ToUInt32(d, body + 4);
                bits = BitConverter.ToUInt16(d, body + 14);
            }
            else if (id == "data")
            {
                var pcm = new byte[size];
                Array.Copy(d, body, pcm, 0, size);
                return channels > 0 && rate > 0 && bits > 0
                    ? (true, channels, rate, bits, pcm)
                    : (false, 0, 0, 0, Array.Empty<byte>());
            }

            pos = body + size + (size % 2); // 块长为奇数时补一字节对齐
        }

        return (false, 0, 0, 0, Array.Empty<byte>());
    }

    private static void WriteWavHeader(byte[] buf, ushort channels, uint rate, ushort bits, int dataLen)
    {
        var byteRate = rate * channels * (bits / 8u);
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(buf, 0);
        BitConverter.GetBytes(36 + dataLen).CopyTo(buf, 4);
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(buf, 8);
        BitConverter.GetBytes(16).CopyTo(buf, 16);
        BitConverter.GetBytes((ushort)1).CopyTo(buf, 20); // PCM
        BitConverter.GetBytes(channels).CopyTo(buf, 22);
        BitConverter.GetBytes(rate).CopyTo(buf, 24);
        BitConverter.GetBytes(byteRate).CopyTo(buf, 28);
        BitConverter.GetBytes((ushort)(channels * (bits / 8))).CopyTo(buf, 32);
        BitConverter.GetBytes(bits).CopyTo(buf, 34);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(buf, 36);
        BitConverter.GetBytes(dataLen).CopyTo(buf, 40);
    }

    private static (byte[]? Data, string? Error, string Note) ConcatMp3(IReadOnlyList<(byte[] Data, string Name)> samples)
    {
        var parts = new List<byte[]>();
        (int Rate, int Channels)? head = null;

        foreach (var (data, name) in samples)
        {
            var frames = StripMp3Tags(data);
            var info = ReadMp3FrameInfo(frames);
            if (info is null)
            {
                return (null, $"这段 mp3 里没找到有效的音频帧（{name}）", string.Empty);
            }

            head ??= (info.Value.Rate, info.Value.Channels);
            if (info.Value.Rate != head.Value.Rate || info.Value.Channels != head.Value.Channels)
            {
                return (null, $"这些 mp3 的采样率/声道不一致（{name} 是 {info.Value.Rate}Hz/{info.Value.Channels}声道）—— " +
                              "不同码率可以，采样率/声道不同会在接缝处咔噃，请先统一", string.Empty);
            }

            parts.Add(frames);
        }

        var total = parts.Sum(p => p.Length);
        var outBuf = new byte[total];
        var pos = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, outBuf, pos, p.Length);
            pos += p.Length;
        }

        var approx = head is null ? 0 : total * 8.0 / 128000; // 按 128kbps 粗估（CBR 时很准）
        return (outBuf, null, $"把 {samples.Count} 段 mp3 按帧拼成了一段（约 {approx:F0} 秒；要求同采样率/声道，码率可以不同）");
    }

    /// <summary>剥掉 ID3v2 头（开头）与 ID3v1 尾（最后 128 字节的 TAG 块）——它们夹在中间会让解码器吃苦头。</summary>
    private static byte[] StripMp3Tags(byte[] d)
    {
        var start = 0;
        if (d.Length > 10 && d[0] == 'I' && d[1] == 'D' && d[2] == '3')
        {
            // ID3v2 大小是 4 个“synchsafe”字节：每字节只用低 7 位
            var size = ((d[6] & 0x7F) << 21) | ((d[7] & 0x7F) << 14) | ((d[8] & 0x7F) << 7) | (d[9] & 0x7F);
            start = Math.Min(d.Length, 10 + size);
        }

        var end = d.Length;
        if (end - start > 128 && d[end - 128] == 'T' && d[end - 127] == 'A' && d[end - 126] == 'G')
        {
            end -= 128;
        }

        // 从第一个看起来像帧同步的位置开始（前面可能还残留 APE/歌词块）
        while (start + 1 < end && !(d[start] == 0xFF && (d[start + 1] & 0xE0) == 0xE0))
        {
            start++;
        }

        var outBuf = new byte[end - start];
        Array.Copy(d, start, outBuf, 0, outBuf.Length);
        return outBuf;
    }

    /// <summary>从第一帧头解析 mp3 参数（采样率索引 / 声道模式）。</summary>
    private static (int Rate, int Channels)? ReadMp3FrameInfo(byte[] d)
    {
        for (var i = 0; i + 4 <= d.Length && i < 8096; i++)
        {
            if (d[i] != 0xFF || (d[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }

            var version = (d[i + 1] >> 3) & 0x03; // 3=MPEG1, 2=MPEG2, 0=MPEG2.5
            var rateIdx = (d[i + 2] >> 2) & 0x03;
            var channelMode = (d[i + 3] >> 6) & 0x03; // 3 = 单声道
            if (rateIdx == 3 || version == 1)
            {
                continue; // 保留值，不是真帧
            }

            var table = version == 3
                ? new[] { 44100, 48000, 32000 }
                : version == 2 ? new[] { 22050, 24000, 16000 } : new[] { 11025, 12000, 8000 };
            return (table[rateIdx], channelMode == 3 ? 1 : 2);
        }

        return null;
    }

    private static string Shorten(string text)
    {
        var one = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return one.Length <= 200 ? one : one[..200] + "…";
    }

    public async Task<(bool Ok, JsonNode? Payload, string? Error)> HealthAsync(CancellationToken ct)
    {
        var baseUrl = BaseUrl;
        if (baseUrl is null)
        {
            return (false, null, "TTS 服务地址没配置（应形如 http://tts:5000）");
        }

        try
        {
            using var resp = await _http.GetAsync($"{baseUrl}/health", ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                return (false, null, $"HTTP {(int)resp.StatusCode}：{(text.Length > 200 ? text[..200] : text)}");
            }

            return (true, JsonNode.Parse(text), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    /// <summary>把设置里的地址规整成 "http://host:port"（无尾部斜杠）；不合法返回 null。</summary>
    private static string? Normalize(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return trimmed.TrimEnd('/');
    }
}
