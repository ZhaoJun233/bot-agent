using System.Net;
using System.Reflection;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Music;
using BotAgent.Services.Qq;

internal static class MusicReceiptChecks
{
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static MusicUseCase UseCase(IQqChatSource source, List<string> logs)
    {
        var http = DispatchProxy.Create<IHttpFetcher, SearchHttp>();
        var catalog = new NeteaseMusicClient(http, () => null, () => "https://example.com", logs.Add);
        var music = new MusicService(null!, catalog, null!, null!, () => new(), "", null!, logs.Add);
        return new(music, source, logs.Add);
    }
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("music use case preserves every structured result without fallback", async () =>
        {
            foreach (var result in new[] { DeliveryResult.Unknown("synthetic_unknown"), DeliveryResult.Throttled(7),
                DeliveryResult.Rejected("synthetic_denied"), DeliveryResult.Degraded("10002", "music"), DeliveryResult.Ok("10002") })
            {
                var source = DispatchProxy.Create<IQqChatSource, MusicSource>(); var fake = (MusicSource)(object)source;
                fake.Result = new(result.IsSuccess, result.IsSuccess ? 10002 : 0) { Delivery = result, DeliveredText = result.IsSuccess ? "synthetic-confirmed" : null };
                var sent = await ((IMusicUseCase)UseCase(source, new())).ShareWithReceiptAsync("synthetic-song", true, 10001);
                Require(sent == fake.Result && fake.MusicCalls == 1 && fake.LegacyCalls == 0 && fake.TextCalls == 0, "structured music receipt collapsed or retried");
            }
        });
        await test("music use case rejects contradictory bool success with unknown receipt", async () =>
        {
            var source = DispatchProxy.Create<IQqChatSource, MusicSource>(); var fake = (MusicSource)(object)source;
            fake.Result = new(true) { Delivery = DeliveryResult.Unknown("synthetic_unknown") };
            var sent = await ((IMusicUseCase)UseCase(source, new())).ShareWithReceiptAsync("synthetic-song", false, 10001);
            Require(!sent.Ok && sent.Delivery?.Status == DeliveryStatus.Unknown && fake.TextCalls == 0, "unknown music claimed success");
        });
        await test("music legacy boolean failure retains one link fallback and its receipt", async () =>
        {
            var source = DispatchProxy.Create<IQqChatSource, MusicSource>(); var fake = (MusicSource)(object)source;
            fake.Result = new(false); fake.TextResult = new(false) { Delivery = DeliveryResult.Unknown("synthetic_link_unknown") };
            var sent = await ((IMusicUseCase)UseCase(source, new())).ShareWithReceiptAsync("synthetic-song", true, 10001);
            Require(sent == fake.TextResult && fake.TextCalls == 1 && fake.LastText == "https://music.163.com/song?id=10001", "legacy fallback changed or lost receipt");
        });
        await test("music receipt log contains no song or transmitted text", async () =>
        {
            var source = DispatchProxy.Create<IQqChatSource, MusicSource>(); var fake = (MusicSource)(object)source;
            fake.Result = new(true) { Delivery = DeliveryResult.Degraded("10002", "music"), DeliveredText = "synthetic-transmitted" };
            var logs = new List<string>();
            var sent = await ((IMusicUseCase)UseCase(source, logs)).ShareWithReceiptAsync("synthetic-private-song", true, 10001);
            Require(sent.Ok && logs.All(line => !line.Contains("synthetic-private-song") && !line.Contains("synthetic-transmitted")), "music receipt log leaks body");
        });
    }
    public class SearchHttp : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "SendAsync" && args?[0] is HttpRequestMessage request && request.RequestUri?.AbsolutePath == "/search")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"result\":{\"songs\":[{\"id\":10001,\"name\":\"synthetic-song\"}]}}") });
            throw new InvalidOperationException("unexpected_music_search_call");
        }
    }
    public class MusicSource : DispatchProxy
    {
        public SendResult Result { get; set; } = new(true);
        public SendResult TextResult { get; set; } = new(true);
        public int MusicCalls { get; private set; }
        public int LegacyCalls { get; private set; }
        public int TextCalls { get; private set; }
        public string? LastText { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "get_IsConnected") return true;
            if (method?.Name == "SendMusicWithReceiptAsync") { MusicCalls++; return Task.FromResult(Result); }
            if (method?.Name == "SendMusicAsync") { LegacyCalls++; return Task.FromResult(Result.Ok); }
            if (method?.Name == "SendTextAsync") { TextCalls++; LastText = (string)args![2]!; return Task.FromResult(TextResult); }
            throw new InvalidOperationException("unexpected_music_source_call");
        }
    }
}
