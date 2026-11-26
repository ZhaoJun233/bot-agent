using System.Text.Json.Nodes;
using BotAgent.Services.Music;

internal static class ShareParserChecks
{
    public static Task RunAsync(Func<string, Func<Task>, Task> test)
        => test("share-parser-and-fallback", () =>
        {
            var miniAppJson = """{"app":"com.tencent.miniapp_01","prompt":"[QQ小程序]示例标题","meta":{"detail_1":{"desc":"描述","qqdocurl":"https://example.com/item","url":"m.q.qq.com/a/s/synthetic"}}}""";
            var miniApp = MusicShareParser.TryParseCard(miniAppJson);
            if (miniApp?.PageUrl != "https://example.com/item")
                throw new InvalidOperationException("miniapp prefers qqdocurl over schemeless url");

            var bareSchemeJson = """{"meta":{"detail_1":{"jumpUrl":"https://","qqdocurl":"https://example.com/valid","url":"m.q.qq.com/a/s/synthetic"}}}""";
            var bareScheme = MusicShareParser.TryParseCard(bareSchemeJson);
            if (bareScheme?.PageUrl != "https://example.com/valid")
                throw new InvalidOperationException("bare https:// does not shadow valid qqdocurl");

            var multiMetaJson = """{"meta":{"detail_1":{"title":"主卡片","url":"m.q.qq.com/a/s/synthetic"},"extra":{"url":"https://example.com/unrelated"}}}""";
            var multiMeta = MusicShareParser.TryParseCard(multiMetaJson);
            if (multiMeta?.PageUrl != "m.q.qq.com/a/s/synthetic")
                throw new InvalidOperationException("secondary meta object does not hijack primary card url");

            var emptyMusicSeg = new JsonObject { ["type"] = "music", ["data"] = new JsonObject() };
            if (MusicShareParser.TryParseSegment(emptyMusicSeg) is not null)
                throw new InvalidOperationException("empty music segment returns null");

            var emptyJsonSeg = new JsonObject { ["type"] = "json", ["data"] = new JsonObject { ["data"] = """{"app":"com.example.unknown","prompt":"[分享]"}""" } };
            if (MusicShareParser.TryParseSegment(emptyJsonSeg) is not null)
                throw new InvalidOperationException("empty json or blank [分享] prompt returns null");

            return Task.CompletedTask;
        });
}
