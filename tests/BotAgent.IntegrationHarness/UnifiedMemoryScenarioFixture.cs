using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    // A real Host process, not linked product sources. All traffic terminates at local mocks.
    private sealed class UnifiedMemoryFixture : IDisposable
    {
        public const long Owner = 10002;
        public const long Other = 10003;
        public const long Source = 10004;
        public const long Target = 10005;
        public const long Unrelated = 10006;
        public const string Preference = "ReplyLength: brief";
        public const string MoodSentinel = "M153_DERIVED_MOOD";
        public const string VibeSentinel = "M153_DERIVED_VIBE";
        public const string AgentSentinel = "M153_DERIVED_AGENT";
        public const string LegacySentinel = "M153_LEGACY_PROFILE";
        public const string UnscopedLegacySentinel = "M153_UNSCOPED_LEGACY";
        public const string ForeignAccountSentinel = "M153_FOREIGN_ACCOUNT_LEGACY";
        public const string ForeignInstanceSentinel = "M153_FOREIGN_INSTANCE_LEGACY";
        public const string SourceScope = "scope:qq.private/legacy//group/10004/~";
        public const string TargetScope = "scope:qq.private/legacy//group/10005/~";
        private readonly string _dataDir = NewDataDir("memory153");
        private readonly int _wsPort = FreePort(13453);
        private readonly int _healthPort = FreePort(18453);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromMinutes(8));
        private BotProcess? _bot;
        private MockProtocol? _protocol;
        private long _messageId = 153000;
        public MockOpenAi ChatModel { get; } = new(FreePort(17453));
        public MockOpenAi AgentModel { get; } = new(FreePort(17454));
        public MockProtocol Protocol => _protocol ?? throw new InvalidOperationException("memory153 not connected");
        public int HealthPort => _healthPort;
        public bool KeepHistory { get; set; }

        public UnifiedMemoryFixture()
        {
            // Only seed our newly allocated empty directory; never import or inspect real profiles.
            var profileDir = Path.Combine(_dataDir, "data", "member_profiles");
            Directory.CreateDirectory(profileDir);
            // Fixed v1 external-format vectors for qq.private/legacy/empty instance,
            // subject 10002, group 10004, null thread. Do not call the product key helper:
            // a changed or collapsed provenance namespace must break this real-Host test.
            var seeds = new[]
            {
                (File: "unscoped", Uid: Owner.ToString(), Marker: UnscopedLegacySentinel),
                (File: "exact", Uid: "legacy-profile:v1:D65F1B6124E01775289C81074F1BE3436D7ACC0A625097EFAB8E1B9F1093D16A", Marker: LegacySentinel),
                (File: "foreign-account", Uid: "legacy-profile:v1:BEC63748244AB82CFAF7B45EB8A387D49B706567B9563684F0AF8A7A6F504F43", Marker: ForeignAccountSentinel),
                (File: "foreign-instance", Uid: "legacy-profile:v1:AFC67295EC5E0F0272523B1475FF83A9DC53CB68C4A1A27B3468E80B84FA8C23", Marker: ForeignInstanceSentinel)
            };
            foreach (var seed in seeds)
            {
                var profile = new JsonObject
                {
                    ["Uid"] = seed.Uid, ["Name"] = "群友A", ["Messages"] = new JsonArray(),
                    ["Summaries"] = new JsonArray(new JsonObject
                    {
                        ["Scope"] = "group:10004", ["Text"] = seed.Marker + " ReplyLength: brief",
                        ["ThroughSeq"] = 0, ["UpdatedUnix"] = DateTimeOffset.Now.AddHours(-2).ToUnixTimeSeconds(),
                        ["FoldedCount"] = 1
                    })
                };
                File.WriteAllText(Path.Combine(profileDir, seed.File + ".json"), profile.ToJsonString());
            }
            ChatModel.Start();
            AgentModel.Start();
        }

        public async Task StartAsync()
        {
            // Unlike the legacy common StartBot, strip ALL product aliases before setting synthetic ones.
            // Do not consume QQCHAT_BOT_DLL: this scenario must execute this worktree's candidate.
            var dll = Path.GetFullPath(Path.Combine(RepoRoot, "src", "BotAgent.Headless", "bin", "Release", "net10.0", "BotAgent.Headless.dll"));
            if (!File.Exists(dll)) throw new FileNotFoundException("Build this worktree's Release Headless first.");
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _dataDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add(dll);
            foreach (var key in psi.Environment.Keys.ToArray())
                if (key.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
                    || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("HEALTH_PORT", StringComparison.OrdinalIgnoreCase))
                    psi.Environment.Remove(key);
            foreach (var (key, value) in EnvironmentForMocks()) psi.Environment[key] = value;
            _bot = new BotProcess(psi);
            await WaitForPortAsync(_wsPort, _stop.Token); // Never dump process/chat logs on failure.
            await WaitForPortAsync(_healthPort, _stop.Token);
            _protocol = new MockProtocol { SelfId = 10001 };
            await _protocol.ConnectReverseAsync($"ws://127.0.0.1:{_wsPort}", _stop.Token);
            var handshake = await _protocol.WaitForActionAsync("get_login_info", TimeSpan.FromSeconds(15)) is not null;
            if (!handshake)
            {
                Console.WriteLine($"M153 startup shape: open={Protocol.IsConnected}, exited={_bot.HasExited}, exit={_bot.ExitCode}");
                foreach (var line in _bot.OutputLines.Where(line => Regex.IsMatch(line, "ERROR|Exception|Unhandled|Traceback")))
                    Console.WriteLine(Regex.Replace(line, "[^\u0000-\u007F]+", "<non-ascii>"));
            }
            Require("M153 host connects to synthetic OneBot", handshake);
        }

        private Dictionary<string, string> EnvironmentForMocks() => new()
        {
            ["BOTAGENT_DATA_DIR"] = _dataDir,
            ["BOTAGENT_API_KEY"] = "sk-mock",
            ["BOTAGENT_BASE_URL"] = ChatModel.BaseUrl,
            ["BOTAGENT_MODEL"] = "mock-model",
            ["BOTAGENT_FAST_REPLY"] = "0",
            ["BOTAGENT_QQ_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["BOTAGENT_QQ_ONEBOT_URL"] = $"http://127.0.0.1:{_wsPort}",
            ["BOTAGENT_QQ_UIN"] = "10001",
            ["BOTAGENT_HEALTH_PORT"] = _healthPort.ToString(),
            ["BOTAGENT_HEALTH_BIND"] = "127.0.0.1",
            ["BOTAGENT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["QQCHAT_PANEL_PASSWORD"] = SyntheticPanelPassword,
            ["BOTAGENT_LOG_FILE"] = "0",
            ["BOTAGENT_VERBOSE"] = "0",
            ["BOTAGENT_MCP_ENABLED"] = "0",
            ["BOTAGENT_OFFICIAL"] = "0",
            ["BOTAGENT_FEISHU"] = "0",
            ["BOTAGENT_UNIFIED_MEMORY_ADMIN"] = new JsonObject { ["PlatformId"] = "qq.private", ["AccountScope"] = "legacy", ["InstanceId"] = "", ["NativeUserId"] = Owner.ToString() }.ToJsonString(),
            ["QQCHAT_WHITELIST"] = "10002,10003,10004,10005,10006",
            ["QQCHAT_PRIVATE_COOLDOWN"] = "0",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_SPLIT_REPLIES"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_PROACTIVE"] = "0",
            ["QQCHAT_PROFILE_SUMMARY"] = "0",
            ["QQCHAT_STICKERS"] = "0",
            ["QQCHAT_ENABLE_MUSIC"] = "0",
            ["QQCHAT_LINK_PREVIEW"] = "0",
            ["QQCHAT_WEB_SEARCH"] = "0",
            ["QQCHAT_ENABLE_VOICE"] = "0",
            ["QQCHAT_AGENT"] = "1",
            ["QQCHAT_NATURAL_AGENT"] = "1",
            ["QQCHAT_AGENT_USERS"] = Owner.ToString(),
            ["QQCHAT_AGENT_SERVER"] = "1",
            ["QQCHAT_AGENT_SERVER_URL"] = AgentModel.BaseUrl,
            ["BOTAGENT_AGENT_SERVER_KEY"] = "sk-mock-agent",
            ["QQCHAT_AGENT_SERVER_MODEL"] = "mock-agent-model",
            ["QQCHAT_AGENT_SERVER_WORKDIR"] = _dataDir,
            ["QQCHAT_AGENT_SERVER_TOOLS"] = "qq",
            ["QQCHAT_AGENT_SERVER_QQ_ACTIONS"] = "like",
            ["QQCHAT_AGENT_SERVER_DOCKER"] = "0",
            ["QQCHAT_AGENT_SERVER_CONTEXT"] = KeepHistory ? "1" : "0",
            ["QQCHAT_AGENT_PROGRESS"] = "0"
        };

        public async Task RestartAsync()
        {
            _protocol?.Dispose();
            _protocol = null;
            if (_bot is not null) await _bot.StopAsync();
            _bot?.Dispose();
            _bot = null;
            await StartAsync();
        }

        public async Task<string> CommandAsync(string command, long? group = null, long user = Owner)
        {
            var before = Protocol.ActionsReceived.Count;
            var chatBefore = ChatModel.Requests.Count;
            var agentBefore = AgentModel.Requests.Count;
            var modelsBefore = chatBefore + agentBefore;
            await SendAsync(command, group, user, mention: false);
            Require("M153 command returns through original transport", await WaitUntilAsync(
                () => SentAfter(before, group, user).Count > 0, TimeSpan.FromSeconds(20)));
            if (ChatModel.Requests.Count + AgentModel.Requests.Count != modelsBefore)
            {
                foreach (var (kind, requests) in new[] { ("chat", ChatModel.Requests.Skip(chatBefore)), ("agent", AgentModel.Requests.Skip(agentBefore)) })
                    foreach (var request in requests)
                        Console.WriteLine($"M153 command-model race shape: model={kind}, sessionTitle={SystemText(request).StartsWith("[会话标题]", StringComparison.Ordinal)}, containsCommand={RequestText(request).Contains(command)}, markers={string.Join(",", Regex.Matches(RequestText(request), "M153_[A-Z0-9_]+").Select(match => match.Value).Distinct())}");
            }
            Check("M153 command never reaches model", ChatModel.Requests.Count + AgentModel.Requests.Count == modelsBefore);
            return string.Join("\n", SentAfter(before, group, user));
        }

        public async Task<JsonObject> ChatAsync(string marker, long group, long user = Owner, bool poison = false)
        {
            var before = Protocol.ActionsReceived.Count;
            var requestStart = ChatModel.Requests.Count;
            var response = new JsonObject { ["suitability"] = 100, ["reply"] = marker + "_OK" };
            if (poison)
            {
                response["mood"] = MoodSentinel;
                response["vibe"] = "热闹";
                response["vibeNote"] = VibeSentinel;
            }
            ChatModel.AddRule(marker, response.ToJsonString());
            await SendAsync(marker, group, user, mention: true);
            Require("M153 chat reaches mock model", await WaitUntilAsync(
                () => ChatModel.Requests.Skip(requestStart).Any(r => UserTexts(r).Any(t => t.Contains(marker))), TimeSpan.FromSeconds(30)));
            var request = ChatModel.Requests.Skip(requestStart).First(r => UserTexts(r).Any(t => t.Contains(marker)));
            Require("M153 chat reply reaches allowed conversation", await WaitUntilAsync(
                () => SentAfter(before, group, user).Any(t => t.Contains(marker + "_OK")), TimeSpan.FromSeconds(30)));
            return request;
        }

        public async Task<JsonObject> AgentAsync(string marker, long group, string reply, bool blocked = false, bool continuation = false, string? initialTool = null)
        {
            var before = Protocol.ActionsReceived.Count;
            var requestStart = AgentModel.Requests.Count;
            var titleStart = ChatModel.Requests.Count;
            AgentModel.AddRule(marker, initialTool is null ? [reply] : [initialTool, reply]);
            await SendAsync((continuation ? "//接着 " : "//") + marker, group, Owner, mention: false);
            Require("M153 agent reaches separate mock model", await WaitUntilAsync(
                () => AgentModel.Requests.Skip(requestStart).Any(r => UserTexts(r).Any(t => t.Contains(marker))), TimeSpan.FromSeconds(30)));
            var request = AgentModel.Requests.Skip(requestStart).First(r => UserTexts(r).Any(t => t.Contains(marker)));
            var expected = blocked ? "受控记忆任务不能调用外部工具" : JsonNode.Parse(reply)?["final"]?.GetValue<string>();
            Require("M153 agent returns final or policy refusal", await WaitUntilAsync(
                () => SentAfter(before, group, Owner).Any(t => expected is not null && t.Contains(expected)), TimeSpan.FromSeconds(30)));
            // Ordinary Agent completion emits a title-model request AFTER sending its final.
            // Observe that actual side effect before the next command snapshots global model counts.
            if (!HasMemory(request)) await AwaitOrdinaryTitleAsync(titleStart);
            return request;
        }

        private async Task AwaitOrdinaryTitleAsync(int requestStart)
        {
            // Previous ordinary turns were drained; the scenario is serial and background
            // summaries are disabled. Match the NEW title request, not its truncated digest.
            bool Matches(JsonObject request) => SystemText(request).StartsWith("[会话标题]", StringComparison.Ordinal);
            var observed = await WaitUntilAsync(() => ChatModel.Requests.Skip(requestStart).Any(Matches), TimeSpan.FromSeconds(20));
            if (!observed)
                foreach (var request in ChatModel.Requests.Skip(requestStart))
                    Console.WriteLine($"M153 title boundary shape: sessionTitle={SystemText(request).StartsWith("[会话标题]", StringComparison.Ordinal)}, markers={string.Join(",", Regex.Matches(RequestText(request), "M153_[A-Z0-9_]+").Select(match => match.Value).Distinct())}");
            Require("M153 ordinary Agent post-final title request reaches deterministic boundary", observed);
            var title = ChatModel.Requests.Skip(requestStart).First(Matches);
            Check("M153 post-final model work is title-only and has no memory commands or derived final",
                !RequestText(title).Contains("//memory") && !RequestText(title).Contains(AgentSentinel));
        }

        public async Task NaturalAgentAttemptAsync(string marker, long group, bool expectBlocked = true)
        {
            var before = AgentModel.Requests.Count;
            var sentBefore = Protocol.ActionsReceived.Count;
            var requestStart = ChatModel.Requests.Count;
            var started = DateTimeOffset.UtcNow;
            ChatModel.AddRule(marker, new JsonObject
            {
                ["suitability"] = 100, ["action"] = "agent", ["agentTask"] = marker + "_DERIVED",
                ["agentAck"] = "M153_NATURAL_ACK"
            }.ToJsonString());
            AgentModel.AddRule(marker + "_DERIVED", MemoryFinal(marker + "_FINAL"));
            await SendAsync(marker, group, Owner, mention: true);
            Require("M153 natural-agent attack reaches real chat model", await WaitUntilAsync(
                () => ChatModel.Requests.Skip(requestStart).Any(r => UserTexts(r).Any(t => t.Contains(marker))), TimeSpan.FromSeconds(30)));
            // Poll a shape-only completed trace, not a fixed sleep or private chat/history endpoint.
            JsonNode? completedTrace = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!deadline.IsCancellationRequested)
            {
                var (status, body) = await PanelGetAsync($"http://127.0.0.1:{_healthPort}/api/traces?limit=20");
                var traces = (JsonNode.Parse(body) as JsonObject)?["traces"] as JsonArray;
                completedTrace = status == 200 ? traces?.FirstOrDefault(t =>
                    t?["startedAt"]?.GetValue<long>() >= started.ToUnixTimeMilliseconds()
                    && t?["nodes"] is JsonArray nodes && nodes.Any(n => n?["kind"]?.GetValue<string>() == "Model")) : null;
                if (completedTrace is not null) break;
                try { await Task.Delay(50, deadline.Token); } catch (OperationCanceledException) { break; }
            }
            Require("M153 natural-agent turn completes at public trace boundary", completedTrace is not null);
            var dispatched = ((JsonArray)completedTrace!["nodes"]!).Any(n => n?["reason"]?.GetValue<string>() == "natural_agent");
            if (expectBlocked)
            {
                Check("M153 memory-derived turn cannot schedule natural Agent", !dispatched);
                Check("M153 model cannot derive Agent task from legacy/unified memory", AgentModel.Requests.Count == before);
            }
            else
            {
                Check("M153 non-memory natural Agent really dispatches", dispatched);
                Require("M153 natural Agent baseline really reaches model and sends final", await WaitUntilAsync(
                    () => AgentModel.Requests.Skip(before).Any(r => UserTexts(r).Any(t => t.Contains(marker + "_DERIVED")))
                        && SentAfter(sentBefore, group, Owner).Any(t => t.Contains(marker + "_FINAL")), TimeSpan.FromSeconds(30)));
                await AwaitOrdinaryTitleAsync(requestStart);
            }
        }

        private Task SendAsync(string text, long? group, long user, bool mention)
            => group is long id
                ? Protocol.SendGroupMessageAsync(id, user, "群友A", text, ++_messageId, mentionBot: mention, ct: _stop.Token)
                : Protocol.SendPrivateMessageAsync(user, "群友A", text, ++_messageId, ct: _stop.Token);

        private List<string> SentAfter(int before, long? group, long user) => Protocol.ActionsReceived.Skip(before)
            .Where(a => a["action"]?.GetValue<string>() == (group is null ? "send_private_msg" : "send_group_msg")
                && a["params"]?[group is null ? "user_id" : "group_id"]?.GetValue<long>() == (group ?? user))
            .Select(MessageText).ToList();

        public static string FactId(string review)
        {
            var match = Regex.Match(review, @"//memory (?:confirm|show) ([a-f0-9]{32})");
            Require("M153 private review exposes own fact handle", match.Success);
            return match.Groups[1].Value;
        }

        public static string Confirmation(string review)
        {
            var match = Regex.Match(review, @"//memory confirm [a-f0-9]{32} [0-9]+ [a-f0-9]{32} allow-source (?:accept-group-audience|private-only)");
            Require("M153 private review exposes exact confirmation", match.Success);
            return match.Value;
        }

        public static void Require(string id, bool passed)
        {
            Check(id, passed);
            if (!passed) throw new InvalidOperationException(id + " (synthetic details withheld)");
        }

        public void Dispose()
        {
            _protocol?.Dispose();
            _bot?.Dispose();
            ChatModel.Dispose();
            AgentModel.Dispose();
            _stop.Dispose();
        }
    }
}
