using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services.Net;
using BotAgent.Services.Resilience;

namespace BotAgent.Services.Voice;

/// <summary>
/// 云端语音转写（STT）宿主传输客户端：
/// 1) 读取 <c>preset.feature.voice</c> 声明式设置（<c>sttEnabled</c>/<c>sttEndpointUrl</c>/<c>sttModel</c>/<c>sttApiKey</c>/<c>sttMaxSeconds</c>）；
/// 2) 走独立 SSRF-safe <see cref="IHttpFetcher"/> + <see cref="ToolCircuitBreaker"/>（<c>stt</c>）；
/// 3) 强制 ≤ 8 MiB 入站音频硬上限与逐跳重定向校验；
/// 4) 隐私红线：日志只写音频字节数、字符长度与固定原因码，绝不打印转写正文或上游原始错误体。
/// </summary>
public sealed class CloudSttClient(
    IHttpFetcher http,
    Func<bool> allowPrivateOutbound,
    Func<AppSettings> readAppSettings,
    Func<IReadOnlyDictionary<string, JsonElement>?> readPluginSettings,
    Action<string> log,
    ToolCircuitBreaker? circuit = null,
    Func<string?>? resolveFallbackApiKey = null)
{
    public const int MaxAudioBytes = 8 * 1024 * 1024;
    private const int MaxRedirects = 3;
    private const int MaxTranscriptChars = 4000;

    public bool IsEnabled
    {
        get
        {
            var values = readPluginSettings();
            return values is not null && ReadBool(values, "sttEnabled", false);
        }
    }

    public int MaxDurationSeconds
    {
        get
        {
            var values = readPluginSettings();
            return values is null ? 60 : Math.Clamp(ReadInt(values, "sttMaxSeconds", 60), 5, 120);
        }
    }

    public async Task<VoiceTranscriptionOutcome> TranscribeAsync(byte[] audioData, string fileName, CancellationToken ct)
    {
        if (!IsEnabled)
            return new(false, null, "stt_disabled");
        if (audioData is not { Length: > 0 } || audioData.Length > MaxAudioBytes)
            return new(false, null, "audio_invalid_or_too_large");

        var endpoint = ResolveEndpointUrl();
        if (string.IsNullOrWhiteSpace(endpoint))
            return new(false, null, "stt_endpoint_unconfigured");

        if (!SafeUrl.TryValidate(endpoint, allowPrivateOutbound(), out var endpointUri, out var ssrfWhy))
        {
            log($"[Voice] STT endpoint blocked reason={ssrfWhy}");
            return new(false, null, "stt_endpoint_ssrf_blocked");
        }

        if (circuit is null)
            return (await TranscribeCoreAsync(endpointUri, audioData, fileName, ct).ConfigureAwait(false)).Outcome;

        VoiceTranscriptionOutcome? faultOutcome = null;
        var run = await circuit.RunAsync(async token =>
        {
            var (outcome, transientFault) = await TranscribeCoreAsync(endpointUri, audioData, fileName, token).ConfigureAwait(false);
            if (transientFault)
            {
                faultOutcome = outcome;
                throw new TimeoutException(outcome.ReasonCode);
            }
            return outcome;
        }, ct).ConfigureAwait(false);

        if (run.Succeeded && run.Value is not null)
            return run.Value;
        if (faultOutcome is not null)
            return faultOutcome;

        log($"[Voice] STT circuit refused reason={run.ReasonCode}");
        return new(false, null, run.ReasonCode ?? "stt_circuit_open");
    }

    public async Task<(byte[]? Data, string FileName, string ReasonCode)> DownloadAudioAsync(
        string rawUrl,
        string fallbackFileName,
        CancellationToken ct)
    {
        var allowPrivate = allowPrivateOutbound();
        if (!SafeUrl.TryValidate(rawUrl, allowPrivate, out var currentUri, out var ssrfWhy))
        {
            log($"[Voice] STT audio url blocked reason={ssrfWhy}");
            return (null, fallbackFileName, "audio_url_ssrf_blocked");
        }

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (IsRedirect(resp.StatusCode) && resp.Headers.Location is { } location)
                {
                    if (hop == MaxRedirects)
                        return (null, fallbackFileName, "audio_too_many_redirects");
                    var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    if (!SafeUrl.TryValidate(nextUri.AbsoluteUri, allowPrivate, out var safeNext, out _))
                        return (null, fallbackFileName, "audio_redirect_ssrf_blocked");
                    currentUri = safeNext;
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                    return (null, fallbackFileName, $"audio_http_{(int)resp.StatusCode}");
                if (resp.Content.Headers.ContentLength is { } len && len > MaxAudioBytes)
                    return (null, fallbackFileName, "audio_too_large");

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxAudioBytes)
                        return (null, fallbackFileName, "audio_too_large");
                    ms.Write(buffer, 0, read);
                }

                var data = ms.ToArray();
                if (data.Length == 0)
                    return (null, fallbackFileName, "audio_empty");

                var resolvedName = Path.GetFileName(currentUri.LocalPath);
                if (string.IsNullOrWhiteSpace(resolvedName) || !Path.HasExtension(resolvedName))
                    resolvedName = string.IsNullOrWhiteSpace(fallbackFileName) ? "voice.wav" : fallbackFileName;
                return (data, resolvedName, "ok");
            }

            return (null, fallbackFileName, "audio_too_many_redirects");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[Voice] STT audio download failed type={ex.GetType().Name}");
            return (null, fallbackFileName, "audio_download_failed");
        }
    }

    private async Task<(VoiceTranscriptionOutcome Outcome, bool TransientFault)> TranscribeCoreAsync(
        Uri endpointUri,
        byte[] audioData,
        string fileName,
        CancellationToken ct)
    {
        var values = readPluginSettings();
        var model = values is not null ? ReadString(values, "sttModel") : null;
        if (string.IsNullOrWhiteSpace(model)) model = "whisper-1";

        var explicitUrl = values is not null ? ReadString(values, "sttEndpointUrl") : null;
        var appSettings = readAppSettings();
        var apiKey = values is not null ? ReadString(values, "sttApiKey") : null;
        if (string.IsNullOrWhiteSpace(apiKey)
            && (string.IsNullOrWhiteSpace(explicitUrl) || IsSameOrigin(endpointUri, appSettings.ModelBaseUrl)))
        {
            apiKey = resolveFallbackApiKey?.Invoke()?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = appSettings.ApiKey?.Trim();
        }

        var safeName = string.IsNullOrWhiteSpace(fileName) ? "voice.mp3" : Path.GetFileName(fileName.Trim());
        if (!Path.HasExtension(safeName)) safeName += ".mp3";

        try
        {
            using var form = new MultipartFormDataContent();
            var audioPart = new ByteArrayContent(audioData);
            audioPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(audioPart, "file", safeName);
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent("json"), "response_format");

            using var req = new HttpRequestMessage(HttpMethod.Post, endpointUri) { Content = form };
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var bodyText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                var transient = status >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests;
                log($"[Voice] STT failed status={status} body_bytes={bodyText.Length}");
                return (new(false, null, $"stt_http_{status}"), transient);
            }

            var node = JsonNode.Parse(bodyText);
            var rawText = (node?["text"]?.GetValue<string>() ?? string.Empty).Trim();
            if (rawText.Length == 0)
            {
                log($"[Voice] STT empty transcript bytes={audioData.Length}");
                return (new(false, null, "stt_empty_transcript"), false);
            }

            var clean = MessageMarkers.EscapeExternalControlTags(rawText);
            if (clean.Length > MaxTranscriptChars)
                clean = clean[..MaxTranscriptChars] + "…";

            log($"[Voice] STT ok bytes={audioData.Length} chars={clean.Length}");
            return (new(true, clean, "ok"), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log($"[Voice] STT request exception type={ex.GetType().Name}");
            return (new(false, null, "stt_request_failed"), true);
        }
    }

    private static bool IsSameOrigin(Uri endpointUri, string? modelBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(modelBaseUrl)
            || !Uri.TryCreate(modelBaseUrl.Trim(), UriKind.Absolute, out var modelUri))
            return false;
        return string.Equals(endpointUri.Scheme, modelUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(endpointUri.Host, modelUri.Host, StringComparison.OrdinalIgnoreCase)
            && endpointUri.Port == modelUri.Port;
    }

    private string? ResolveEndpointUrl()
    {
        var values = readPluginSettings();
        var explicitUrl = values is not null ? ReadString(values, "sttEndpointUrl") : null;
        if (!string.IsNullOrWhiteSpace(explicitUrl))
            return explicitUrl.Trim();

        var modelBase = readAppSettings().ModelBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(modelBase))
            return null;
        return modelBase.TrimEnd('/') + "/audio/transcriptions";
    }

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var el)) return fallback;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
            _ => fallback
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out var el)) return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n)) return n;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var s)) return s;
        return fallback;
    }

    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> values, string key)
    {
        if (!values.TryGetValue(key, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString()?.Trim() : null;
    }
}
