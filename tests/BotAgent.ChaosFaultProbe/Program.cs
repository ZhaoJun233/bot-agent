using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Rendering;
using BotAgent.Services.Ops;
using BotAgent.Services.Resilience;
using BotAgent.Services.OneBot;

namespace BotAgent.ChaosFaultProbe;

public static class Program
{
    public static async Task<int> Main()
    {
        BotAgent.Adapters.Time.ClockBindings.InitializePlatforms();
        var dataRoot = Path.Combine(Path.GetTempPath(), "botagent-chaos-fault-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", dataRoot);
        var passed = 0;
        var failed = 0;
        var failures = new List<string>();

        try
        {
            var clock = new SyntheticClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
                        var persistedTransitions = new List<ProviderCircuitSnapshot>();
            var runner = new ProviderFailoverRunner(
                new[]
                {
                    new ProviderCandidate("primary", 0),
                    new ProviderCandidate("backup", 1),
                },
                clock.Now,
                cooldown: TimeSpan.FromSeconds(120),
                onSnapshotChanged: persistedTransitions.Add);

            var primaryCalls = 0;
            var backupCalls = 0;
            async Task<ProviderCallResult<string>> PrimaryOrBackup(ProviderCandidate provider, CancellationToken _)
            {
                await Task.Yield();
                if (provider.Id == "primary")
                {
                    primaryCalls++;
                    return ProviderCallResult<string>.Failure("upstream_503");
                }

                backupCalls++;
                return ProviderCallResult<string>.Success("synthetic-backup-response");
            }

            for (var i = 0; i < 3; i++)
            {
                var result = await runner.RunAsync(PrimaryOrBackup);
                Check(result.Succeeded && result.ProviderId == "backup" && result.FallbackHops == 1,
                    "连续 503 由备用 Provider 接管", ref passed, ref failed, failures);
            }

            var primarySnapshot = runner.Snapshots().Single(x => x.ProviderId == "primary");
                        Check(primarySnapshot.State == ProviderCircuitState.Open && primarySnapshot.ConsecutiveHardFailures == 3,
                "连续 3 次硬错误后 L1 熔断器进入 open", ref passed, ref failed, failures);
            Check(persistedTransitions.Any(x => x.ProviderId == "primary" && x.State == ProviderCircuitState.Open),
                "运行时熔断状态变更可交给持久化回调", ref passed, ref failed, failures);

            var resumedRunner = new ProviderFailoverRunner(
                new[] { new ProviderCandidate("primary", 0) },
                clock.Now,
                cooldown: TimeSpan.FromSeconds(120),
                initialSnapshots: new[] { primarySnapshot });
            Check(resumedRunner.Snapshots().Single().State == ProviderCircuitState.Open &&
                  resumedRunner.Snapshots().Single().ConsecutiveHardFailures == 3,
                "故障转移执行器启动时恢复持久化熔断状态", ref passed, ref failed, failures);

            var skippedPrimary = await runner.RunAsync(PrimaryOrBackup);
            Check(skippedPrimary.Succeeded && skippedPrimary.ProviderId == "backup" && primaryCalls == 3,
                "熔断期间跳过主 Provider 且不增加失败调用", ref passed, ref failed, failures);

            clock.Advance(TimeSpan.FromSeconds(121));
            var recovered = await runner.RunAsync<string>((provider, _) =>
            {
                if (provider.Id == "primary")
                {
                    primaryCalls++;
                    return Task.FromResult(ProviderCallResult<string>.Success("synthetic-recovered-response"));
                }

                backupCalls++;
                return Task.FromResult(ProviderCallResult<string>.Success("synthetic-backup-response"));
            });
            Check(recovered.Succeeded && recovered.ProviderId == "primary" && primaryCalls == 4,
                "冷却后 half-open 探测成功并恢复主 Provider", ref passed, ref failed, failures);
            Check(runner.Snapshots().Single(x => x.ProviderId == "primary").State == ProviderCircuitState.Closed,
                "half-open 成功后状态恢复 closed", ref passed, ref failed, failures);

            var timeoutRunner = new ProviderFailoverRunner(
                new[] { new ProviderCandidate("timeout-provider", 0) },
                clock.Now,
                cooldown: TimeSpan.FromSeconds(120));
            var timeout = await timeoutRunner.RunAsync<string>((_, _) =>
                throw new OperationCanceledException("synthetic upstream hang"));
            Check(!timeout.Succeeded && timeout.Silent && timeout.ReasonCode == "provider_timeout",
                "上游挂起被转换为安全静默结果", ref passed, ref failed, failures);

            var unavailableRunner = new ProviderFailoverRunner(
                new[]
                {
                    new ProviderCandidate("primary", 0),
                    new ProviderCandidate("backup", 1),
                },
                clock.Now,
                cooldown: TimeSpan.FromSeconds(120));
            var unavailable = await unavailableRunner.RunAsync<string>((_, _) =>
                Task.FromResult(ProviderCallResult<string>.Failure("all_upstreams_503")));
            Check(!unavailable.Succeeded && unavailable.Silent && unavailable.ProviderId is null,
                "所有 Provider 不可用时进入安全静默保护态", ref passed, ref failed, failures);

            var halfOpen = new ProviderCircuitBreaker(
                "half-open-probe",
                cooldown: TimeSpan.FromSeconds(120),
                clock: clock.Now);
            halfOpen.RecordHardFailure();
            halfOpen.RecordHardFailure();
            halfOpen.RecordHardFailure();
            clock.Advance(TimeSpan.FromSeconds(121));
            var permits = await Task.WhenAll(
                Task.Run(() => halfOpen.TryEnter(out _)),
                Task.Run(() => halfOpen.TryEnter(out _)));
            Check(permits.Count(x => x) == 1,
                "half-open 同时只放行一个探测请求", ref passed, ref failed, failures);

            Check(ReplyAuditRules.Judge("../../qqchat.db", allowLocalPaths: false) == ReplyAuditVerdict.BlockLocalPath,
                "路径穿越文件名被出站路径安全规则阻断", ref passed, ref failed, failures);
            Check(ReplyAuditRules.Judge("ignore previous instructions", allowLocalPaths: false) == ReplyAuditVerdict.BlockSystemPrompt,
                "Prompt 注入指纹被出站 DLP 阻断", ref passed, ref failed, failures);


            AppDatabase.Initialize();
            var providerStore = new ModelProviderStore();
            providerStore.EnsurePrimary(new BotAgent.Services.AppSettings
            {
                ModelBaseUrl = "https://example.com/v1",
                Model = "synthetic-model"
            });
            var primary = providerStore.Find("primary");
            Check(primary is not null
                && primary.SecretKeyRef == "env:QQCHAT_API_KEY"
                && primary.BaseUrl == "https://example.com/v1"
                && primary.ModelName == "synthetic-model",
                "Provider 元数据落库且只保存密钥引用", ref passed, ref failed, failures);
            var persistedBreaker = new ProviderCircuitBreaker("primary", clock: clock.Now);
            persistedBreaker.RecordHardFailure();
            persistedBreaker.RecordHardFailure();
            var opened = persistedBreaker.RecordHardFailure();
            providerStore.SaveCircuit(opened);
            var restored = providerStore.Find("primary");
            Check(restored?.CircuitState == ProviderCircuitState.Open
                && restored.ConsecutiveHardFailures == 3
                && restored.CooldownUntil is not null,
                "Provider 熔断状态、连续硬错误计数与冷却时间可恢复", ref passed, ref failed, failures);
            var cooldownBeforeRefresh = restored?.CooldownUntil;
            providerStore.EnsurePrimary(new BotAgent.Services.AppSettings
            {
                ModelBaseUrl = "https://example.com/v2",
                Model = "synthetic-model-v2"
            });
            var refreshed = providerStore.Find("primary");
            Check(refreshed?.BaseUrl == "https://example.com/v2"
                && refreshed.ModelName == "synthetic-model-v2"
                && refreshed.CircuitState == ProviderCircuitState.Open
                && refreshed.ConsecutiveHardFailures == 3
                && refreshed.CooldownUntil == cooldownBeforeRefresh,
                "重复登记 Provider 只更新元数据且不重置熔断状态", ref passed, ref failed, failures);
            var missingProviderError = false;
            try
            {
                providerStore.SaveCircuit(new ProviderCircuitSnapshot(
                    "missing-provider", ProviderCircuitState.Open, 3, clock.Now(), ProbeInFlight: false));
            }
            catch (InvalidOperationException)
            {
                missingProviderError = true;
            }
            Check(missingProviderError,
                "未登记 Provider 保存熔断状态时显式失败", ref passed, ref failed, failures);
            var restoredBreaker = new ProviderCircuitBreaker("primary", clock: clock.Now);
            restoredBreaker.Restore(new ProviderCircuitSnapshot(
                "primary", ProviderCircuitState.Open, restored?.ConsecutiveHardFailures ?? 0,
                restored?.CooldownUntil, ProbeInFlight: true));
            Check(restoredBreaker.Snapshot().State == ProviderCircuitState.Open
                && !restoredBreaker.Snapshot().ProbeInFlight,
                "持久化熔断快照可恢复且不带入旧探测锁", ref passed, ref failed, failures);
            Check(AppDatabase.Scalar<long>("PRAGMA user_version") >= 6,
                "Provider 注册表迁移版本已推进", ref passed, ref failed, failures);
            Check(AppDatabase.Scalar<long>("SELECT COUNT(1) FROM pragma_table_info('model_providers') WHERE name = 'consecutive_hard_failures'") == 1,
                "Provider 连续硬错误计数字段已幂等迁移", ref passed, ref failed, failures);

var chatHttp = new SyntheticHttpFetcher();
            var chatRunner = new ProviderFailoverRunner(
                new[]
                {
                    new ProviderCandidate("primary", 0),
                    new ProviderCandidate("backup", 1),
                },
                clock.Now,
                cooldown: TimeSpan.FromSeconds(120));
            var chatClient = new OpenAiClient(
                new SettingsBox(new AppSettings { ApiKey = "synthetic-key", Model = "default-model" }),
                chatHttp,
                chatHttp,
                new SyntheticImageDownloader(),
                new SyntheticModelTransport(),
                chatRunner,
                new Dictionary<string, ModelProviderRoute>(StringComparer.Ordinal)
                {
                    ["primary"] = new ModelProviderRoute("primary", "https://primary.example/v1", "primary-model", "synthetic-key"),
                    ["backup"] = new ModelProviderRoute("backup", "https://backup.example/v1", "backup-model", "synthetic-key"),
                });
            var chatResponse = await chatClient.CompleteChatAsync(
                "caller-model", "synthetic-system",
                new[] { ("user", "synthetic-request") }, 128, 0.2);
            Check(chatResponse == "synthetic-backup-response"
                && chatHttp.PrimaryCalls == 2
                && chatHttp.BackupCalls == 1,
                $"CompleteChatAsync 通过 Provider 注册表故障转移到备用模型（response={chatResponse ?? "<null>"}, primary={chatHttp.PrimaryCalls}, backup={chatHttp.BackupCalls}）", ref passed, ref failed, failures);
            var toolBreaker = new ToolCircuitBreaker(
                "synthetic-search",
                clock: clock.Now,
                hardTimeout: TimeSpan.FromMilliseconds(20),
                cooldown: TimeSpan.FromMinutes(5));
            var toolCalls = 0;
            for (var i = 0; i < 3; i++)
            {
                var timedOut = await toolBreaker.RunAsync(async token =>
                {
                    toolCalls++;
                    await Task.Delay(100, token);
                    return "never-reached";
                });
                Check(!timedOut.Succeeded && timedOut.TimedOut && timedOut.ReasonCode == "tool_timeout",
                    "L2 工具单次调用超时返回明确原因码", ref passed, ref failed, failures);
            }

            Check(toolBreaker.Snapshot().State == ToolCircuitState.Open
                && toolBreaker.Snapshot().ConsecutiveTimeouts == 3,
                "L2 工具连续 3 次超时后进入 open", ref passed, ref failed, failures);
            var blockedToolCall = await toolBreaker.RunAsync(_ =>
            {
                toolCalls++;
                return Task.FromResult("should-not-run");
            });
            Check(!blockedToolCall.Succeeded && blockedToolCall.ReasonCode == "tool_circuit_open" && toolCalls == 3,
                "L2 熔断期间不重试也不执行工具", ref passed, ref failed, failures);

            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
            var recoveredToolCall = await toolBreaker.RunAsync(_ => Task.FromResult("synthetic-recovered-tool"));
            Check(recoveredToolCall.Succeeded && recoveredToolCall.Value == "synthetic-recovered-tool"
                && toolBreaker.Snapshot().State == ToolCircuitState.Closed,
                "L2 冷却后 half-open 探测成功并恢复 closed", ref passed, ref failed, failures);

            // GitHub Issue #23: 调用方在执行过程中取消应原样抛出，且不污染失败/超时计数，也不锁死 HalfOpen 探测
            using (var callerCts = new CancellationTokenSource())
            {
                var callerCancelled = false;
                try
                {
                    await toolBreaker.RunAsync<string>(async token =>
                    {
                        callerCts.Cancel();
                        token.ThrowIfCancellationRequested();
                        await Task.CompletedTask;
                        return "should-cancel";
                    }, callerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    callerCancelled = true;
                }
                Check(callerCancelled, "L2 工具调用方执行中主动取消时原样抛出 OperationCanceledException", ref passed, ref failed, failures);
                Check(toolBreaker.Snapshot().State == ToolCircuitState.Closed
                    && toolBreaker.Snapshot().ConsecutiveTimeouts == 0
                    && !toolBreaker.Snapshot().ProbeInFlight,
                    "L2 工具调用方主动取消不污染超时计数与熔断状态", ref passed, ref failed, failures);
            }

            // 测试 ProviderFailoverRunner 在执行中取消时原样抛出且不污染计数
            using (var providerCts = new CancellationTokenSource())
            {
                var runnerCancelled = false;
                var testRunner = new ProviderFailoverRunner(
                    new[] { new ProviderCandidate("p1", 0) },
                    clock.Now);
                try
                {
                    await testRunner.RunAsync<string>((_, token) =>
                    {
                        providerCts.Cancel();
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult(ProviderCallResult<string>.Success("ok"));
                    }, providerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    runnerCancelled = true;
                }
                Check(runnerCancelled, "L1 ProviderFailoverRunner 调用方执行中主动取消时原样抛出", ref passed, ref failed, failures);
                Check(testRunner.Snapshots()[0].State == ProviderCircuitState.Closed
                    && testRunner.Snapshots()[0].ConsecutiveHardFailures == 0
                    && !testRunner.Snapshots()[0].ProbeInFlight,
                    "L1 ProviderFailoverRunner 调用方主动取消不记录硬错误", ref passed, ref failed, failures);
            }
            var riskBackoff = new ProtocolRiskBackoff(
                now: clock.Now,
                duration: TimeSpan.FromMinutes(30));
            riskBackoff.ObserveFailure("group:10001", "send_group_msg", 1200, "synthetic rate limit");
            var ordinaryText = riskBackoff.EvaluateText("group:10001", directAddress: false, "synthetic ordinary reply");
            Check(!ordinaryText.Allowed && ordinaryText.ReasonCode == "protocol_backoff",
                "L3 风控退避期间阻断普通文本发送", ref passed, ref failed, failures);
            var directText = riskBackoff.EvaluateText("group:10001", directAddress: true, new string('x', 400));
            Check(directText.Allowed && directText.Restricted && directText.Text.Length <= ProtocolRiskBackoff.MaxShortTextChars,
                "L3 退避期间仅允许被点名的单条短文本", ref passed, ref failed, failures);
            var otherTenant = riskBackoff.EvaluateText("group:10002", directAddress: false, "synthetic other tenant");
            Check(otherTenant.Allowed, "L3 退避按会话隔离，不阻塞其他租户", ref passed, ref failed, failures);
            clock.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));
            Check(riskBackoff.EvaluateText("group:10001", directAddress: false, "synthetic recovered").Allowed,
                "L3 默认 30 分钟后自动恢复发送", ref passed, ref failed, failures);
            var protocolRisk = new ProtocolRiskBackoff(now: clock.Now);
            var protocol = new SyntheticOneBotTransport();
            using (var gateway = new OneBotGateway(new SettingsBox(new AppSettings()), protocolRisk, _ => protocol))
            {
                gateway.Start();
                var first = await gateway.SendTextAsync(true, 10001, "synthetic first attempt");
                var suppressed = await gateway.SendTextAsync(true, 10001, "synthetic ordinary attempt");
                Check(!first.Ok && !suppressed.Ok && protocol.SendsFor(10001) == 1,
                    "NapCat 风控响应触发会话级发送退避", ref passed, ref failed, failures);
                var voiceAllowed = await gateway.SendVoiceAsync(true, 10001, "https://example.com/synthetic-audio");
                var imageAllowed = await gateway.SendImageAsync(true, 10001, new byte[] { 1 });
                Check(!voiceAllowed && !imageAllowed && protocol.SendsFor(10001) == 1,
                    "NapCat 风控退避期间语音与表情包不触达协议端", ref passed, ref failed, failures);
                var direct = await gateway.SendTextAsync(true, 10001, new string('x', 400), directAddress: true);
                Check(direct.Ok && protocol.LastTextLength(10001) <= ProtocolRiskBackoff.MaxShortTextChars,
                    "NapCat 风控退避期间 @ 回复限为短文本", ref passed, ref failed, failures);
                var unaffected = await gateway.SendTextAsync(true, 10002, "synthetic unaffected tenant");
                Check(unaffected.Ok && protocol.SendsFor(10002) == 1,
                    "NapCat 退避不影响其他会话", ref passed, ref failed, failures);
                var genericFailure = await gateway.SendTextAsync(true, 10003, "synthetic generic failure");
                var noRiskBackoff = await gateway.SendTextAsync(true, 10003, "synthetic next attempt");
                Check(!genericFailure.Ok && noRiskBackoff.Ok && protocol.SendsFor(10003) == 2,
                    "retcode 1200 无明确风险措辞时不误触发退避", ref passed, ref failed, failures);
            }
            var audit = new AuditLogStore();
            audit.Append(new AuditEvent("provider_fallback", "synthetic-probe", "synthetic:tenant-a",
                "{\"provider\":\"backup\",\"reason\":\"upstream_503\"}", "2.1"));
            Check(audit.Verify().Valid, "Provider 降级事件写入审计链并可校验", ref passed, ref failed, failures);

            var traces = new TraceArchiveStore();
            traces.Append(new TurnTrace(
                "synthetic-chaos-trace",
                "synthetic:tenant-a",
                clock.Now(),
                "fallback",
                20000,
                new[] { new TurnNode(TurnNodeKind.Model, "fallback", 20000, ReasonCode: "provider_timeout") }));
            Check(new TraceArchiveStore().Snapshot().Count == 1,
                "超时/降级轨迹写入 trace_archive", ref passed, ref failed, failures);

            var report = new
            {
                probe = "ChaosFaultProbe",
                environment = "synthetic",
                assertions = passed,
                failed,
                primaryCalls,
                backupCalls,
                failureReasons = failures,
                status = failed == 0 ? "passed" : "failed",
            };
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ChaosFaultProbe failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
            }
            catch
            {
                // 临时夹具清理失败不能覆盖探针结果。
            }
        }
    }

    private static void Check(
        bool condition,
        string name,
        ref int passed,
        ref int failed,
        ICollection<string> failures)
    {
        if (condition)
        {
            passed++;
            return;
        }

        failed++;
        failures.Add(name);
    }

private sealed class SyntheticHttpFetcher : IHttpFetcher
    {
        public int PrimaryCalls { get; private set; }
        public int BackupCalls { get; private set; }
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            => Respond(request);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption _, CancellationToken ct = default)
            => Respond(request);

        public Task<HttpResponseMessage> GetAsync(string _, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(new NotSupportedException());

        public Task<HttpResponseMessage> GetAsync(Uri _, HttpCompletionOption __, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(new NotSupportedException());

        public Task<HttpResponseMessage> GetAsync(Uri _, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(new NotSupportedException());

        public Task<HttpResponseMessage> GetAsync(string _, HttpCompletionOption __, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(new NotSupportedException());

        public Task<HttpResponseMessage> PostAsync(string _, HttpContent __, CancellationToken ct = default)
            => Task.FromException<HttpResponseMessage>(new NotSupportedException());

        private Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            var isPrimary = string.Equals(request.RequestUri?.Host, "primary.example", StringComparison.OrdinalIgnoreCase);
            if (isPrimary)
            {
                PrimaryCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("synthetic primary unavailable", Encoding.UTF8, "text/plain")
                });
            }

            BackupCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"synthetic-backup-response\"}}]}",
                    Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SyntheticImageDownloader : IImageDownloader
    {
        public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshUrls { get; set; }
        public int CacheHits => 0;
        public int RefreshedCount => 0;
        public Task<(byte[] Data, string Mime, string Ext)?> DownloadBytesAsync(string _, CancellationToken __, long? ___ = null)
            => Task.FromResult<(byte[] Data, string Mime, string Ext)?>(null);
    }

    private sealed class SyntheticModelTransport : IModelTransport
    {
        public Task<BuiltRequest> BuildAsync(
            IReadOnlyList<BotAgent.Domain.Conversation.ChatMessage> _, string __,
            IReadOnlyCollection<long> ___, CancellationToken ____,
            BotAgent.Domain.Reply.SamplingProfile? _____ = null) =>
            Task.FromResult(new BuiltRequest(new JsonObject(), 0, new List<long>()));

        public Task<SendOutcome> SendAsync(
            JsonObject _, int __, IReadOnlyList<long> ___, CancellationToken ____) =>
            Task.FromResult(new SendOutcome("{\"choices\":[]}", false));
    }
    private sealed class SyntheticOneBotTransport : IOneBotTransport
    {
        private readonly Dictionary<long, int> _sends = new();
        private readonly Dictionary<long, int> _lastTextLength = new();
        public event Action<string>? OnText;
        public event Action<bool>? OnStateChanged;
        public bool IsConnected => true;
        public Task StartAsync() => Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
        public int SendsFor(long targetId) => _sends.GetValueOrDefault(targetId);
        public int LastTextLength(long targetId) => _lastTextLength.GetValueOrDefault(targetId);

        public Task SendActionAsync(string action, string paramsJson, string echo, CancellationToken ct = default)
        {
            var payload = JsonNode.Parse(paramsJson)!;
            var target = payload[action == "send_group_msg" ? "group_id" : "user_id"]!.GetValue<long>();
            _sends[target] = SendsFor(target) + 1;
            var message = payload["message"];
            _lastTextLength[target] = message is JsonValue ? message.ToString().Length : 0;
            var firstFailure = _sends[target] == 1 && target is 10001 or 10003;
            var response = new JsonObject
            {
                ["echo"] = echo,
                ["status"] = firstFailure ? "failed" : "ok",
                ["retcode"] = firstFailure ? 1200 : 0,
                ["wording"] = target == 10001 && firstFailure ? "synthetic rate limit" : "synthetic parameter error",
                ["data"] = new JsonObject { ["message_id"] = 9001 }
            };
            OnText?.Invoke(response.ToJsonString());
            return Task.CompletedTask;
        }
    }
    private sealed class SyntheticClock
    {
        private DateTimeOffset _now;

        public SyntheticClock(DateTimeOffset initial) => _now = initial;

        public DateTimeOffset Now() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }
}
