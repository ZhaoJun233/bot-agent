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
using BotAgent.Services.Stickers;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    /// <summary>
    /// 表情包库接口。库是全库共用一份（不分会话）：
    ///   GET  /api/stickers                 列表（含 id、说明、关键词、用过几次）
    ///   GET  /api/stickers/{id}/img        图片本体（面板缩略图用）
    ///   POST /api/stickers/{id}/delete     删除一张（面板手动）
    ///   POST /api/stickers/curate          立即让机器人巡检一遍（自己决定删哪些）
    ///   POST /api/stickers/import          从 QQ 收藏表情导入（机器人自己“添加”）
    /// </summary>
    private async Task HandleStickersAsync(HttpListenerContext context, string path, string method)
    {
        var store = _stickers.Store;
        var rest = path.Length > "/api/stickers".Length ? path["/api/stickers/".Length..] : string.Empty;

        if (rest.Length == 0)
        {
            await WriteJsonAsync(context, 200, new JsonObject
            {
                ["enabled"] = _settings.EnableStickers,
                ["max"] = _settings.StickerLibraryMax,
                ["candidates"] = _settings.StickerCandidates,
                ["curateIntervalSeconds"] = _settings.StickerCurateIntervalSeconds,
                ["count"] = store.Count,
                ["described"] = store.DescribedCount,
                ["pendingDescribe"] = _stickers.PendingDescribe,
                ["describeDone"] = _stickers.DescribeDone,
                ["items"] = new JsonArray(store.Snapshot()
                    .OrderByDescending(s => s.AddedAt)
                    .Select(s => (JsonNode)new JsonObject
                    {
                        ["id"] = s.Id,
                        ["desc"] = s.Desc,
                        ["tags"] = new JsonArray(s.Tags.Select(t => (JsonNode)t).ToArray()),
                        ["uses"] = s.Uses,
                        ["bytes"] = s.Bytes,
                        ["ext"] = s.Ext,
                        ["addedAt"] = s.AddedAt * 1000,
                        ["lastUsedAt"] = s.LastUsedAt * 1000,
                        ["fromUid"] = s.FromUid,
                        ["fromGroup"] = s.FromGroup,
                        ["described"] = s.Described,
                        // true/false = 模型审过“是不是表情包”；null = 还没审（未审的不会被发出去）
                        ["isSticker"] = s.IsSticker
                    })
                    .ToArray())
            });
            return;
        }

        var parts = rest.Split('/', 2);
        var id = Uri.UnescapeDataString(parts[0]);

        // 图片本体：<img> 不能带自定义请求头，所以只能靠 ?token=（与 SSE 同一套约定）
        if (parts.Length == 2 && parts[1].Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            var record = store.Find(id);
            if (record is null || !StickerStore.ExistsOnDisk(record))
            {
                await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "sticker not found" });
                return;
            }

            var bytes = await store.ReadBytesAsync(record);
            var type = record.Ext switch
            {
                "jpg" => "image/jpeg",
                "gif" => "image/gif",
                "webp" => "image/webp",
                _ => "image/png"
            };
            context.Response.Headers["Cache-Control"] = "no-store";
            await WriteBytesAsync(context, 200, type, bytes);
            return;
        }

        if (method != "POST")
        {
            await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
            return;
        }

        // 两段形式：/api/stickers/{id}/delete
        // 注意：单段形式（/api/stickers/curate）下 parts 只有一个元素，
        // 直接读 parts[1] 会抛 IndexOutOfRange（之前就是这么把 curate/import 打挂的）。
        if (parts.Length == 2 && parts[1].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            var ok = store.Remove(id, "面板手动删除");
            await WriteJsonAsync(context, ok ? 200 : 404, new JsonObject
            {
                ["ok"] = ok,
                ["count"] = store.Count
            });
            return;
        }

        // 单段形式：/api/stickers/curate 或 /api/stickers/import
        if (parts.Length == 1)
        {
            switch (id.ToLowerInvariant())
            {
                case "curate":
                    // 别让面板等模型：先回一句，跑完往面板推日志
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            FileLog.Write("Sticker", await _stickers.CurateAsync(force: true));
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn("Sticker", $"手动巡检失败：{ex.Message}");
                        }
                    });
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["started"] = true });
                    return;

                case "import":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            FileLog.Write("Sticker", await _stickers.ImportFromAlbumAsync());
                        }
                        catch (Exception ex)
                        {
                            FileLog.Warn("Sticker", $"导入收藏表情失败：{ex.Message}");
                        }
                    });
                    await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true, ["started"] = true });
                    return;
            }
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "not found", ["path"] = path });
    }
}
