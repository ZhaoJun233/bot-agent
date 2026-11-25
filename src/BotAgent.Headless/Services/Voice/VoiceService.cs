using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ports;
using BotAgent.Services.Resilience;

namespace BotAgent.Services.Voice;

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
public sealed class VoiceService : IVoiceSynthesis
{
    private readonly IHttpFetcher _http;
    private readonly Func<AppSettings> _settings;
    private readonly ISecretsRepository _secrets;
    private readonly Action<string> _log;
    private readonly ToolCircuitBreaker? _circuit;

    public VoiceService(IHttpFetcher http, Func<AppSettings> settings, ISecretsRepository secrets, Action<string> log, ToolCircuitBreaker? circuit = null)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _log = log;
        _circuit = circuit;
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

    /// <summary>音调（-12~+12，0 = 不传）。</summary>
    public int Pitch => Math.Clamp(_settings().VoicePitch, -12, 12);

    /// <summary>音量（10~1000 = 0.1~10 倍，0 = 不传）。</summary>
    public int Vol => Math.Clamp(_settings().VoiceVol, 0, 1000);

    /// <summary>情绪（空 = 不传）。</summary>
    public string Emotion => (_settings().VoiceEmotion ?? string.Empty).Trim();

    /// <summary>
    /// 拼出发给协议端的 <c>/speak</c> 绝对地址。text 为空、服务地址不合法时返回 null
    /// （上层据此降级成发文字，而不是把一条空语音发出去）。
    /// </summary>
    /// <param name="emotionOverride">模型按语境给的情绪（null = 用面板默认）。</param>
    /// <param name="pitchOverride">模型按语境给的音调（null = 用面板默认）。</param>
    /// <param name="volOverride">音量（目前只有面板会给；模型不给）。</param>
    public string? BuildSpeakUrl(string text, string? voiceOverride = null, int? speedOverride = null, string? emotionOverride = null, int? pitchOverride = null, int? volOverride = null)
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
        var url = $"{baseUrl}/speak?text={Uri.EscapeDataString(trimmed)}" +
                  $"&voice={Uri.EscapeDataString(voice)}" +
                  $"&speed={speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";

        // 情绪 / 音调 / 音量：**只在显式配了**才带上 —— 不配就与改造前完全一致（云端默认）。
        // 后端认不认是后端的事（OpenAI 兼容那几个只有 speed），编好 URL 交给它就行。
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        // 模型按语境给的值优先；它没说（null）才用面板默认 —— 面板从此是“默认值”而不是唯一来源。
        var emotion = string.IsNullOrWhiteSpace(emotionOverride) ? Emotion : emotionOverride!.Trim();
        var pitch = Math.Clamp(pitchOverride ?? Pitch, -12, 12);
        var vol = Math.Clamp(volOverride ?? Vol, 0, 1000);

        if (emotion.Length > 0)
        {
            url += "&emotion=" + Uri.EscapeDataString(emotion);
        }

        if (pitch != 0)
        {
            url += "&pitch=" + pitch.ToString(ci);
        }

        if (vol > 0)
        {
            url += "&vol=" + (vol / 100.0).ToString("0.##", ci);
        }

        return url;
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
        if (_circuit is null)
        {
            return await SynthesizeCoreAsync(text, voiceOverride, speedOverride, ct).ConfigureAwait(false);
        }

        var run = await _circuit.RunAsync(
            token => SynthesizeCoreAsync(text, voiceOverride, speedOverride, token), ct).ConfigureAwait(false);
        if (run.Succeeded)
        {
            return run.Value;
        }

        _log($"[Voice] TTS 工具调用未完成（{run.ReasonCode}）");
        return (null, run.ReasonCode);
    }

    private async Task<(byte[]? Data, string? Error)> SynthesizeCoreAsync(
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
                // D0-VOICE-ERROR-10：绝不把上游原始错误正文写进日志或返回调用方（避免泄露密钥/金丝雀/内部堆栈）。
                _log($"[Voice] TTS 合成失败 HTTP {(int)resp.StatusCode}（body_bytes={bytes.Length}）");
                return (null, $"HTTP {(int)resp.StatusCode}：上游语音服务返回错误");
            }

            if (bytes.Length < 44 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            {
                return (null, $"TTS 返回的不是 wav（{bytes.Length} 字节，HTTP {resp.Content.Headers.ContentType}）");
            }

            return (bytes, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[Voice] TTS 请求异常：{ex.GetType().Name}");
            return (null, $"TTS 请求异常（{ex.GetType().Name}）");
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
    var key = _secrets.LoadTtsKey();
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
                return (list, $"云端返回 HTTP {(int)resp.StatusCode}");
            }

            var json = TryParseJson(text);
            if (json is null)
            {
                return (list, "云端返回格式异常");
            }
            var status = NodeInt(json["base_resp"]?["status_code"]);
            if (status != 0)
            {
                return (list, $"云端报错 status={status}");
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
            return (list, $"列复刻音色失败（{ex.GetType().Name}）");
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
        // 口径按云端实际要求收紧（实测教训 2026-09-21：ID 写成 “ATR” —— 3 位且大写 —— 云端只回一句
        // 笼统的 2013 invalid params，让人以为是“样本不合格”白查半天）。
        // 所以在本地就拦：**≥8 位、全小写字母/数字/下划线/连字符、以字母开头**。
        if (wanted.Length < 8 || wanted.Length > 64 ||
            !System.Text.RegularExpressions.Regex.IsMatch(wanted, "^[a-z][a-z0-9_-]{7,63}$"))
        {
            return (false, "音色 ID 要 **8~64 位、全小写、以字母开头**（只能用小写字母/数字/下划线/连字符），" +
                           "例如 example_voice_01 —— 云端对不合规的 ID 只回一句笼统的 2013，很容易误判成“样本不合格”");
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
            // 诊断：把样本的**格式魔数与大小**记下来（不记内容）。
            // 为什么要它：云端只回一句 “2013 invalid params”，无法区分“太短 / 不是人声 / 根本不是 mp3”；
            // 线上最常见的是“手机录音/微信语音导出”其实是 m4a/amr/silk，改个后缀就当 mp3 传了 ——
            // 这种一看魔数就穿（真 mp3 开头是 ID3 或 0xFFF*；wav 是 RIFF；m4a 是 ....ftyp；silk 是 #!SILK）。
            var magic = audio.Length >= 12
                ? System.Text.Encoding.ASCII.GetString(audio, 0, 12).Replace("\0", ".")
                : "(太短)";
            _log($"[Voice] 开始复刻：voice_id={wanted}，样本 {audio.Length / 1024}KB，开头字节={magic}");

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
            var uploadJson = TryParseJson(uploadText);
            var fileId = NodeText(uploadJson?["file"]?["file_id"]);
            // 只记形状（不回显 raw_url 这种带签名的东西）：file_id 拿到没、上游有没有报 base_resp 错
            var uploadBase = NodeInt(uploadJson?["base_resp"]?["status_code"]);
            _log($"[Voice] 样本已上传：file_id={(fileId.Length > 0 ? "有" : "无")}（{fileId.Length} 位）" +
                 $"，upload base_resp={uploadBase}" +
                 ((fileId.Length > 0 && !fileId.All(char.IsDigit)) ? "，注意 file_id 不是纯数字" : string.Empty));
            if (!uploadResp.IsSuccessStatusCode || uploadJson is null || string.IsNullOrWhiteSpace(fileId))
            {
                return (false, $"上传样本失败（HTTP {(int)uploadResp.StatusCode}，status={uploadBase}）");
            }

            // ② 建克隆
            // ⚠ 官方 schema 里 <c>file_id</c> 是 **integer(int64)**，不是字符串 ——
            // 当字符串发会被判 2013 invalid params（实测 2026-09-21：样本、ID 全合规也照样失败）。
            // 这里按“能解析成数字就发数字”处理（宽容：万一哪天变成字符串也照发）。
            var cloneBody = new JsonObject { ["voice_id"] = wanted };
            if (long.TryParse(fileId, out var fileIdNum))
            {
                cloneBody["file_id"] = fileIdNum;
            }
            else
            {
                cloneBody["file_id"] = fileId;
            }

            using var cloneReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/voice_clone")
            {
                Content = new StringContent(cloneBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            cloneReq.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var cloneResp = await _http.SendAsync(cloneReq, ct);
            var cloneText = await cloneResp.Content.ReadAsStringAsync(ct);
            var cloneJson = TryParseJson(cloneText);
            var status = NodeInt(cloneJson?["base_resp"]?["status_code"]);
            if (!cloneResp.IsSuccessStatusCode || cloneJson is null || status != 0)
            {
                var why = status switch
                {
                    2013 => "（2013 = 参数/样本不合格：需 mp3/m4a/wav、**10 秒 ~ 5 分钟**、≤ 20MB、单人干净人声；voice_id 只能用字母/数字/下划线/连字符）",
                    2039 => "（2039 = 这个 voice_id 已经克隆过了，换个 ID 或先删旧的）",
                    1004 or 2049 => "（key 与站点不配对：国内站 api.minimaxi.com / 国际站 api.minimax.io，面板「云端接口地址」要跟 key 对应）",
                    2042 => "（账号没有音色复刻权限/额度）",
                    _ => string.Empty
                };
                return (false, $"克隆失败（HTTP {(int)cloneResp.StatusCode}，status {status}）{(why.Length > 0 ? " " + why : string.Empty)}");
            }

            _log($"[Voice] 音色复刻成功：{wanted}（样本 {audio.Length / 1024}KB）");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"复刻失败（{ex.GetType().Name}）");
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

    // 多段样本拼接（WAV / MP3）已迁入 preset.feature.voice 独立包（VoiceSampleConcatenator）。

    /// <summary>
    /// 删掉一个自己复刻的音色（<c>POST /v1/delete_voice</c>，voice_type=voice_cloning）。
    /// 为什么要做进面板：克隆音色有额度、且“同时只能有一个同名 ID”——旧的不删影响后面的尝试；
    /// 而且官方删是**只能删克隆音色**，系统音色删不了（删了也不影响预置音色）。
    /// </summary>
    public async Task<(bool Ok, string? Error)> DeleteClonedVoiceAsync(string voiceId, CancellationToken ct)
    {
        var baseUrl = CloneBaseUrl();
        var key = CloneKey();
        if (baseUrl is null)
        {
            return (false, "当前服务商不支持音色复刻（也就没有“删除克隆音色”这套接口）");
        }

        if (key is null)
        {
            return (false, "还没配云端 TTS 密钥");
        }

        var wanted = (voiceId ?? string.Empty).Trim();
        if (wanted.Length == 0)
        {
            return (false, "没指定要删哪个音色");
        }

        try
        {
            var body = new JsonObject { ["voice_type"] = "voice_cloning", ["voice_id"] = wanted };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/delete_voice")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            var json = TryParseJson(text);
            var status = NodeInt(json?["base_resp"]?["status_code"]);
            if (!resp.IsSuccessStatusCode || json is null || status != 0)
            {
                return (false, $"删除失败（HTTP {(int)resp.StatusCode}，status {status}）");
            }

            _log($"[Voice] 已删掉复刻音色：{wanted}");
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"删除失败（{ex.GetType().Name}）");
        }
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
                return (false, null, $"HTTP {(int)resp.StatusCode}（body_bytes={text.Length}）");
            }

            var parsed = TryParseJson(text);
            return parsed is not null ? (true, parsed, null) : (false, null, "TTS 健康接口返回非 JSON 响应");
        }
        catch (Exception ex)
        {
            return (false, null, $"TTS 健康检查异常（{ex.GetType().Name}）");
        }
    }

    private static JsonNode? TryParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
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
