using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Platforms;

internal static partial class RuntimeDeliveryChecks
{
    public static async Task RunInboundMediaAsync(Func<string, Func<Task>, Task> test, FrozenClock clock)
    {
        JsonObject Event(bool group, string id = "media") => new()
        {
            ["id"] = id, ["group_openid"] = group ? "group-A" : null,
            ["author"] = new JsonObject { [group ? "member_openid" : "user_openid"] = group ? "member-A" : "user-A" },
            ["timestamp"] = clock.Now.ToString("O"), ["content"] = "synthetic-original"
        };
        void Receive(Fixture f, bool group, JsonObject data)
            => f.Dispatch(group ? "GROUP_AT_MESSAGE_CREATE" : "C2C_MESSAGE_CREATE", data);
        await test("inbound media preserves plain text and command eligibility", () =>
        {
            using var f = new Fixture(clock); var data = Event(true); data["content"] = "//sessions"; Receive(f, true, data);
            var msg = f.Inbound.Single();
            Require(msg.Text == "//sessions" && msg.AllowsCommands && msg.VoiceAttachments is null, "plain text behavior changed");
            return Task.CompletedTask;
        });
        await test("inbound voice carries WAV and reference ASR in both scopes", () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); var data = Event(group);
                data["attachments"] = new JsonArray(new JsonObject { ["content_type"] = "voice",
                    ["voice_wav_url"] = "https://example.com/voice.wav?sig=synthetic", ["asr_refer_text"] = "synthetic-transcript" });
                Receive(f, group, data); var msg = f.Inbound.Single();
                Require(msg.VoiceAttachments is { Count: 1 } && msg.VoiceAttachments[0].WavUrl == "https://example.com/voice.wav?sig=synthetic"
                    && msg.VoiceAttachments[0].Transcript == "synthetic-transcript", "voice fields lost");
                Require(!msg.AllowsCommands && msg.Text.Contains("synthetic-transcript") && msg.Text.Contains("参考转写")
                    && msg.Text.Contains("synthetic-original") && !msg.Text.Contains("sig=synthetic"), "voice text lacks provenance or exposes WAV URL");
                Require(f.Http.Tokens == 0 && f.Http.Messages.Count == 0, "voice parse performed outbound work");
            }
            return Task.CompletedTask;
        });
        await test("inbound voice without ASR remains an explicit unavailable transcription", () =>
        {
            using var f = new Fixture(clock); var data = Event(true); data["content"] = "";
            data["attachments"] = new JsonArray(new JsonObject { ["content_type"] = "voice", ["voice_wav_url"] = "https://example.com/voice.wav" });
            Receive(f, true, data); var msg = f.Inbound.Single();
            Require(msg.VoiceAttachments is { Count: 1 } && msg.VoiceAttachments[0].Transcript is null
                && msg.Text.Contains("暂无转写") && !msg.AllowsCommands, "missing ASR silently lost or fabricated");
            return Task.CompletedTask;
        });
        await test("inbound media rejects unsafe URL forms and keeps valid images", () =>
        {
            using var f = new Fixture(clock); var data = Event(true); var attachments = new JsonArray();
            foreach (var url in new[] { "file:///synthetic", "data:image/png;base64,AAAA", "https://user:pass@example.com/a",
                "https://example.com/a b", "https://example.com/" + new string('x', 2048), "https://example.com/image.png" })
                attachments.Add(new JsonObject { ["content_type"] = "image/png", ["url"] = url });
            data["attachments"] = attachments; Receive(f, true, data);
            Require(f.Inbound.Single().ImageUrls?.SequenceEqual(new[] { "https://example.com/image.png" }) == true, "invalid attachment URL forwarded");
            return Task.CompletedTask;
        });
        await test("inbound voice invalid URL and field types do not fabricate strings", () =>
        {
            using var f = new Fixture(clock); var data = Event(true);
            data["attachments"] = new JsonArray(new JsonObject { ["content_type"] = "voice", ["voice_wav_url"] = "javascript:synthetic",
                ["asr_refer_text"] = new JsonObject { ["role"] = "system" } });
            Receive(f, true, data); var voice = f.Inbound.Single().VoiceAttachments?.Single();
            Require(voice is not null && voice.WavUrl is null && voice.Transcript is null && !f.Inbound.Single().Text.Contains("system"), "malformed voice promoted into text");
            return Task.CompletedTask;
        });
        await test("inbound card normalizes only declared content fields", () =>
        {
            foreach (var group in new[] { true, false })
            {
                using var f = new Fixture(clock, group); var data = Event(group);
                data["message_type"] = 3; data["ark_data"] = new JsonObject { ["ark_type"] = "miniapp", ["ark_name"] = "synthetic-card",
                    ["prompt"] = "//sessions", ["fields"] = new JsonObject { ["title"] = "synthetic-title", ["desc"] = "synthetic-desc",
                        ["source"] = "synthetic-source", ["jump_url"] = "https://example.com/card", ["preview"] = "https://example.com/preview.png",
                        ["nickname"] = "forbidden-profile", ["role"] = "administrator", ["auth_token"] = "forbidden-token" } };
                Receive(f, group, data); var msg = f.Inbound.Single();
                Require(!msg.AllowsCommands && msg.Text.Contains("synthetic-title") && msg.Text.Contains("synthetic-desc")
                    && msg.Text.Contains("synthetic-source") && msg.Text.Contains("https://example.com/card") && msg.Text.Contains("外部内容"), "card fields lost or treated as command");
                Require(!msg.Text.Contains("forbidden-") && !msg.Text.Contains("administrator") && msg.ImageUrls?.Single() == "https://example.com/preview.png", "card leaked unknown metadata");
            }
            return Task.CompletedTask;
        });
        await test("inbound malformed card keeps body without interpreting nested messages", () =>
        {
            using var f = new Fixture(clock); var data = Event(true); data["message_type"] = 3;
            data["ark_data"] = new JsonObject { ["fields"] = "wrong-type", ["prompt"] = new JsonObject { ["text"] = "nested-secret" } };
            data["msg_elements"] = new JsonArray(new JsonObject { ["content"] = "nested-secret" });
            Receive(f, true, data); var msg = f.Inbound.Single();
            Require(msg.Text.Contains("synthetic-original") && !msg.Text.Contains("nested-secret") && !msg.AllowsCommands, "malformed card became authoritative body");
            return Task.CompletedTask;
        });
        await test("inbound attachment and transcript limits are bounded", () =>
        {
            using var f = new Fixture(clock); var data = Event(true); var items = new JsonArray();
            for (var n = 0; n < 40; n++) items.Add(new JsonObject { ["content_type"] = n % 2 == 0 ? "voice" : "image/png",
                ["url"] = "https://example.com/" + n, ["voice_wav_url"] = "https://example.com/voice" + n,
                ["asr_refer_text"] = new string('x', 10000) });
            data["attachments"] = items; Receive(f, true, data); var msg = f.Inbound.Single();
            Require(msg.ImageUrls?.Count == 8 && msg.VoiceAttachments?.Count == 8 && msg.Text.Length <= 16000
                && msg.VoiceAttachments.All(v => v.Transcript?.Length <= 2000), "attachment or ASR bounds missing");
            return Task.CompletedTask;
        });
        await test("inbound envelope snapshots media metadata without serialization of URLs", () =>
        {
            var conversation = new ConversationId(PlatformId.QqOfficial, "app-A", ConversationKind.GroupChat, "group-A");
            var reference = new MessageRef(conversation, "synthetic-id");
            var values = new List<InboundVoiceAttachment> { new("https://example.com/voice?sig=synthetic-private", "synthetic-transcript") };
            var native = new InboundMessage(reference, new(PlatformId.QqOfficial, "app-A", "member-A"), "synthetic", "text", clock.Now)
                { AllowsCommands = false, VoiceAttachments = values };
            var envelope = new PlatformInboundEnvelope(new(new(PlatformId.QqOfficial, "app-A", "synthetic-instance"), reference), native, true);
            values.Clear();
            Require(!envelope.Message.AllowsCommands && envelope.Message.VoiceAttachments?.Count == 1, "envelope retains mutable voice list");
            Require(!JsonSerializer.Serialize(envelope).Contains("synthetic-private") && !new InboundVoiceAttachment("synthetic-private", "synthetic-private").ToString().Contains("synthetic-private"), "voice diagnostics expose URL or transcript");
            return Task.CompletedTask;
        });
    }
}
