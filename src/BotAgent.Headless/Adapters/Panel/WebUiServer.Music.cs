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
using BotAgent.Services.Music;
using BotAgent.Services.Net;
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>
    /// 面板内的网易云扫码登录（代理到自建 API 的 /login/qr/*）。
    /// 为什么放在面板里：手机号/密码登录要暴露账号密码，扫码最干净；
    /// 而且登录态是存在自建 API 那边的，面板只是把二维码拿过来展示、帮忙轮询。
    /// </summary>
    private async Task HandleNeteaseQrAsync(HttpListenerContext context, string path, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        var baseUrl = _settings.NeteaseBaseUrl.TrimEnd('/');
        var stamp = Clock.Now.ToUnixTimeMilliseconds();

        try
        {
            if (path.EndsWith("/check", StringComparison.OrdinalIgnoreCase))
            {
                var body = await ReadJsonAsync(context);
                var key = body?["key"]?.GetValue<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "缺少 key" });
                    return;
                }

                var check = await GetJsonFromAsync($"{baseUrl}/login/qr/check?key={Uri.EscapeDataString(key)}&timestamp={stamp}");

                // 扫码成功（803）时把登录态存下来：上游会在这条响应里给 cookie。
                // 为什么必须存：cookie 本来只活在自建 API 容器的进程内存里 ——
                // 容器一重建（升级镜像 / compose up 重创）就得重新扫码，管理员反馈的“老是掉登录”就是这个。
                // 存进库（secrets 表，权限 600）之后，每轮请求直接带 cookie（见 NeteaseMusicClient），
                // 与那个容器活着不活着无关；重启机器人也不会丢。
                if (TryReadInt(check?["code"]) == 803 &&
                    check?["cookie"] is JsonValue cookieValue && cookieValue.TryGetValue<string>(out var freshCookie) &&
                    !string.IsNullOrWhiteSpace(freshCookie))
                {
                    var saved = _secrets.SaveNeteaseCookie(freshCookie);
                    // 立即生效（音乐客户端每轮现读）：走发布点换引用（SettingsHotReload.PatchSettings），不就地改共享实例；
                    // 它已经在 secrets 表里落过盘，所以不触发重建、也不再往 settings.json 写一遍。
                    _settingsHotReload.PatchSettings(s => s.NeteaseCookie = freshCookie);
                    check["saved"] = saved;
                    FileLog.Write("Music", saved
                        ? $"网易云扫码登录成功，登录态已存进库里（{freshCookie.Length} 字，重启/重建容器都不丢）"
                        : "网易云扫码登录成功，但登录态落盘失败（仍会用在本次进程内）");
                }

                await WriteJsonAsync(context, 200, check ?? new JsonObject { ["error"] = "上游无响应" });
                return;
            }

            /// <summary>宽容地读一个整数（上游有时给字符串 "803"，不确定就别让它把整条链路弄挂）。</summary>
            static int? TryReadInt(JsonNode? node)
            {
                if (node is not JsonValue value)
                {
                    return null;
                }

                if (value.TryGetValue<int>(out var number))
                {
                    return number;
                }

                return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : null;
            }

            // 两步：先拿 key，再让上游生成二维码（qrimg=true 直接回 base64 图）
            var keyJson = await GetJsonFromAsync($"{baseUrl}/login/qr/key?timestamp={stamp}");
            var unikey = keyJson?["data"]?["unikey"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(unikey))
            {
                await WriteJsonAsync(context, 502, new JsonObject
                {
                    ["error"] = "拿不到二维码 key（自建网易云接口不可用？）",
                    ["detail"] = keyJson?.ToJsonString() ?? "(无响应)"
                });
                return;
            }

            var qrJson = await GetJsonFromAsync($"{baseUrl}/login/qr/create?key={Uri.EscapeDataString(unikey)}&qrimg=true&timestamp={stamp}");
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = true,
                ["key"] = unikey,
                ["qrimg"] = qrJson?["data"]?["qrimg"]?.GetValue<string>() ?? string.Empty,
                ["qrurl"] = qrJson?["data"]?["qrurl"]?.GetValue<string>() ?? string.Empty
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    // 面板登录流程的出网（打自建网易云接口；超时短一点，别拖住面板）：见 _neteaseHttp（装配点造）。

    /// <summary>向自建网易云接口发一个 GET 并解析 JSON（仅面板登录流程用）。</summary>
    private async Task<JsonNode?> GetJsonFromAsync(string url)
    {
        using var resp = await _neteaseHttp.GetAsync(url);
        var text = await resp.Content.ReadAsStringAsync();
        try
        {
            return JsonNode.Parse(text);
        }
        catch (Exception)
        {
            return new JsonObject { ["raw"] = text.Length > 300 ? text[..300] : text };
        }
    }

    /// <summary>/api/music/test：把“听音乐”链路真跑一遍（搜歌 → 歌词 → 低码率音源 → 波形分析）。</summary>
    private async Task HandleMusicTestAsync(HttpListenerContext context, string method)
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

        var song = body?["song"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(song) || song.Length > 60)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 song（歌名，可带歌手，≤ 60 字）" });
            return;
        }

        try
        {
            var note = await _music.TestByNameAsync(song, CancellationToken.None);
            var song2 = _music.LastTestHeader;
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = note is not null,
                ["song"] = song2,
                ["note"] = note ?? "没搜到，或这首歌没拿到音源且没有歌词"
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }

    /// <summary>
    /// /api/search/test：跑一次真实联网搜索（模型自带搜索优先，否则走搜索源模板）。
    /// 为什么要这个入口：搜索能不能用跟“服务器 IP、代理支不支持工具”强相关，
    /// 面板上当场跑一次，比在群里碰运气强。
    /// </summary>
    private async Task HandleSearchTestAsync(HttpListenerContext context, string method)
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

        var query = body?["query"]?.GetValue<string>()?.Trim();
        var url = body?["url"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(url))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "请填 query（搜索词）或 url（要读的页面）" });
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                var (text, error) = await _research.TestReadPageAsync(url!, CancellationToken.None);
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["ok"] = text is not null,
                    ["mode"] = "read",
                    ["text"] = text,
                    ["error"] = error
                });
                return;
            }

            var result = await _research.TestSearchAsync(query!, CancellationToken.None);
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["ok"] = result.HasContent,
                ["mode"] = "search",
                ["provider"] = result.Provider,
                ["answer"] = result.Answer,
                ["hits"] = new JsonArray(result.Hits
                    .Select(h => (JsonNode)new JsonObject
                    {
                        ["title"] = h.Title,
                        ["url"] = h.Url,
                        ["snippet"] = h.Snippet
                    })
                    .ToArray()),
                ["note"] = result.Describe(),
                ["error"] = result.Error
            });
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = ex.Message });
        }
    }
}
