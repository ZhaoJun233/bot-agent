using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Voice;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>
    /// /api/voice/test：真合成一句语音（走 TTS 容器 /speak），直接把 wav 字节还给浏览器播。
    /// 为什么返回二进制而不是 JSON+base64：浏览器直接 Blob 播放最省事，也不白扛 33% 的 base64 开销。
    /// 音色/语速可以带参数（面板改了还没保存也能试听）；服务地址一律用已保存的设置 ——
    /// 面板不足以成为“拿任意 URL 去访问”的入口（跟白名单/密码一个道理）。
    /// </summary>
    private async Task HandleVoiceTestAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        JsonNode? body;
        try
        {
            body = await ReadJsonAsync(context);
        }
        catch (Exception)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请求体不是合法 JSON" });
            return;
        }

        var text = body?["text"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 text（要说的一句话）" });
            return;
        }

        if (text.Length > 300)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = $"文本太长（{text.Length} > 300），长文本请改用文字" });
            return;
        }

        var voice = body?["voice"]?.GetValue<string>()?.Trim();
        var speed = body?["speed"] is JsonNode sp && int.TryParse(sp.ToString(), out var parsedSpeed) ? parsedSpeed : (int?)null;

        var (data, error) = await _voice.TestAsync(text, voice, speed, CancellationToken.None);
        if (data is null)
        {
            await WriteJsonAsync(context, 502, new JsonObject { ["error"] = error ?? "合成失败" });
            return;
        }

        context.Response.Headers["Cache-Control"] = "no-store";
        await WriteBytesAsync(context, 200, "audio/wav", data);
    }

    /// <summary>/api/voice/clones：列出云端已复刻的音色（面板点一下就填进「音色」格）。</summary>
    private async Task HandleVoiceClonesAsync(HttpListenerContext context)
    {
        var voice = _voice.Client;
        if (voice is null)
        {
            await WriteJsonAsync(context, 200, new JsonObject { ["voices"] = new JsonArray(), ["error"] = "这个实例没有语音服务" });
            return;
        }

        var (voices, error) = await voice.ListClonedVoicesAsync(CancellationToken.None);
        var arr = new JsonArray();
        foreach (var v in voices)
        {
            arr.Add(v);
        }

        await WriteJsonAsync(context, 200, new JsonObject { ["voices"] = arr, ["error"] = error });
    }

    /// <summary>
    /// POST /api/voice/clone：音色复刻（上传样本 → 建克隆）。
    /// 面板把选中的音频读成 base64 一起发上来：HttpListener 里解 multipart 纯属自找麻烦。
    /// 两种请求体都收：
    ///   • 单段：<c>{"audioBase64":"…","fileName":"a.mp3","voiceId":"zhao_voice_01"}</c>
    ///   • 多段（自动拼接）：<c>{"samples":[{"audioBase64":"…","fileName":"1.mp3"},…],"voiceId":"…"}</c>
    /// 多段是为了“手上只有 5 秒切片”的情况 —— 官方主样本要求 ≥ 10 秒，服务端把几段拼成一段再传。
    /// </summary>
    private async Task HandleVoiceCloneAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "用法：POST /api/voice/clone" });
            return;
        }

        var voice = _voice.Client;
        if (voice is null)
        {
            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = false, ["error"] = "这个实例没有语音服务" });
            return;
        }

        var body = await ReadJsonAsync(context);
        var decoded = new List<(byte[] Data, string Name)>();

        // 多段：面板多选时走这条路
        if (body?["samples"] is JsonArray arr && arr.Count > 0)
        {
            foreach (var node in arr)
            {
                var (bytes, err) = DecodeAudio(node?["audioBase64"]?.GetValue<string>());
                if (err is not null)
                {
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = false, ["error"] = err });
                    return;
                }

                decoded.Add((bytes!, node?["fileName"]?.GetValue<string>() ?? "sample.mp3"));
            }
        }
        else
        {
            var (bytes, err) = DecodeAudio(body?["audioBase64"]?.GetValue<string>());
            if (err is not null)
            {
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = false, ["error"] = err });
                return;
            }

            decoded.Add((bytes!, body?["fileName"]?.GetValue<string>() ?? "sample.mp3"));
        }

        var fileName = decoded.Count == 1 ? decoded[0].Name : $"merged-{decoded.Count}.mp3";
        var voiceId = body?["voiceId"]?.GetValue<string>() ?? string.Empty;

        // 多段先拼（面板上那些 5 秒切片就是走这里）
        string note = string.Empty;
        var finalBytes = decoded[0].Data;
        if (decoded.Count > 1)
        {
            var (merged, concatError, concatNote) = 
                BotAgent.Services.Voice.VoiceService.ConcatSamples(decoded);
            if (concatError is not null || merged is null)
            {
                FileLog.Write("Voice", "面板拼接样本失败：" + concatError);
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = false, ["error"] = concatError });
                return;
            }

            finalBytes = merged;
            note = concatNote;
            FileLog.Write("Voice", $"面板把 {decoded.Count} 段样本拼成一段：{concatNote}（{finalBytes.Length / 1024}KB）");
        }

        var (ok, error) = await voice.CloneVoiceAsync(finalBytes, fileName, voiceId, CancellationToken.None);
        if (ok)
        {
            FileLog.Write("Voice", $"面板复刻音色成功：{voiceId}（样本 {finalBytes.Length / 1024}KB{(note.Length > 0 ? "，" + note : string.Empty)}）");
        }
        else
        {
            FileLog.Write("Voice", "面板复刻音色失败：" + error);
        }

        await WriteJsonAsync(context, 200, new JsonObject
        {
            ["ok"] = ok,
            ["voiceId"] = voiceId,
            ["note"] = note,
            ["error"] = error,
        });
    }

    /// <summary>POST /api/voice/clone/delete：删掉一个自己复刻的音色（请求体 {"voiceId":"…"}）。</summary>
    private async Task HandleVoiceCloneDeleteAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "用法：POST /api/voice/clone/delete" });
            return;
        }

        var voice = _voice.Client;
        if (voice is null)
        {
            await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = false, ["error"] = "这个实例没有语音服务" });
            return;
        }

        var body = await ReadJsonAsync(context);
        var voiceId = (body?["voiceId"]?.GetValue<string>() ?? string.Empty).Trim();
        var (ok, error) = await voice.DeleteClonedVoiceAsync(voiceId, CancellationToken.None);
        FileLog.Write("Voice", ok ? $"面板删掉了复刻音色：{voiceId}" : "面板删除复刻音色失败：" + error);
        await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = ok, ["voiceId"] = voiceId, ["error"] = error });
    }

    /// <summary>面板传来的 base64 → 字节（容忍 <c>data:audio/…;base64,</c> 前缀）。</summary></summary>
    private static (byte[]? Data, string? Error) DecodeAudio(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, "没有选音频文件");
        }

        try
        {
            var comma = raw.IndexOf(',');
            return (Convert.FromBase64String(comma >= 0 ? raw[(comma + 1)..] : raw), null);
        }
        catch (Exception)
        {
            return (null, "音频不是合法的 base64（面板读取失败？）");
        }
    }

    /// <summary>/api/voice/health：把 TTS 服务自己的 /health 透传给面板（活着吗、有哪些音色）。</summary>
    private async Task HandleVoiceHealthAsync(HttpListenerContext context)
    {
        var voice = _voice.Client;
        if (voice is null)
        {
            await WriteJsonAsync(context, 503, new JsonObject { ["ok"] = false, ["error"] = "语音服务还没初始化" });
            return;
        }

        var (ok, payload, error) = await voice.HealthAsync(CancellationToken.None);
        await WriteJsonAsync(context, ok ? 200 : 502, new JsonObject
        {
            ["ok"] = ok,
            ["url"] = voice.BaseUrl,
            ["currentVoice"] = voice.VoiceName,
            ["voices"] = payload?["voices"]?.DeepClone() ?? new JsonArray(),
            ["default"] = payload?["default"]?.ToString(),
            ["error"] = error
        });
    }
}
