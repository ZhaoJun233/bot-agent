using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Rendering;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Reply;

namespace BotAgent.SafetyProbe;

public static partial class Program
{
    private static void ActionNarrationTests()
    {
        Section("Issue #66 · 语义判断 / 自然口语 / 保留锚点（真实客户端，合成 HTTP）");
        var oldLogging = FileLog.WriteToFile;
        FileLog.WriteToFile = false;
        try
        {
            foreach (var input in new[] { "2exp(i·2kπ/5)", "2*3*4", "sin((x+y)/2)", "a*b*c", "a*beta*c", "长*宽*高", "请参考 (1) 和 (2)", "无旁白的回复。" })
            {
                var probe = new ActionFilterProbe(_ => throw new InvalidOperationException("No call expected"));
                var result = QqPlainText.Sanitize(probe.Run(input));
                Check("数学/无候选不发辅助请求：" + input, result == input && probe.Calls == 0, result);
            }
            foreach (var input in new[] { "好的(晃了晃耳朵)我知道了", "（叹了口气）行吧，那就这样", "*伸了个懒腰*准备睡觉", "*（点头）*好的。", "（*点头*）好的。", "(smiles)Hello.", "*smiles*Hello." })
            {
                var probe = new ActionFilterProbe(r => Select(r, [0]));
                var output = probe.Run(input);
                var candidate = probe.LastInput!["candidates"]![0]!["text"]!.GetValue<string>();
                Check("只删语义确认片段：" + input, output == input.Replace(candidate, "") && probe.Calls == 1, output);
                Check("模型能看见候选原文及保留正文", !string.IsNullOrEmpty(candidate) && probe.LastInput["protectedParts"]!.AsArray().Count > 0);
            }
            var rewrite = new ActionFilterProbe(r => Select(r, [0], "我有点累了。"));
            Check("动作可改成自然话语，已有对话保持不变", rewrite.Run("（叹气）先休息吧。") == "我有点累了。先休息吧。");

            foreach (var protectedText in new[]
            {
                "2exp(i·2kπ/5)", "sin((x+y)/2)", "2*3*4", "x + y = z", "a * b * c", "（x ∈ R）", "*δ*",
                "这是（普通解释）", "这是 (beta) 版本", "https://example.com/a_(b)?x=1", "[资料](https://example.com)",
                "`if (x) *y*`", "```csharp\nif (x) { y *= 2; }\n```", "$$f(x)=x*(x+1)$$", "\\(f(x) = x^2\\)"
            })
            {
                var probe = new ActionFilterProbe(r => Select(r, [0]));
                var input = "（点头）" + protectedText;
                Check("移除动作仍完整保留正文：" + protectedText, probe.Run(input) == protectedText && probe.Calls == 1);
            }
            foreach (var ordinary in new[] { "这是（普通说明）", "这是 (beta) 版本", "（摇头）是成语的例子", "*强调*而不是动作" })
            {
                var probe = new ActionFilterProbe(r => Select(r, []));
                Check("模型未确认动作时逐字保留：" + ordinary, probe.Run(ordinary) == ordinary);
            }
            var mixed = new ActionFilterProbe(r => Select(r, [0]));
            Check("混合候选保留未选中的普通括号", mixed.Run("（微笑）这是（普通解释）。") == "这是（普通解释）。");

            foreach (var raw in new[] { "", "not json", "[]", "null", "{}", "{\"reply\":\"改写\"}",
                         "{\"actionIds\":[0],\"reply\":\"\"}", "{\"actionIds\":[-1],\"reply\":\"正文\"}",
                         "{\"actionIds\":[0,0],\"reply\":\"正文\"}", "{\"actionIds\":[\"0\"],\"reply\":\"正文\"}" })
            {
                var probe = new ActionFilterProbe(_ => raw);
                const string input = "（点头）原有对话。";
                Check("坏契约保留原文：" + raw, probe.Run(input) == input);
            }
            var changedNegation = new ActionFilterProbe(_ => "{\"actionIds\":[0],\"reply\":\"我同意。\"}");
            Check("否定句被重写时回退原文", changedNegation.Run("（摇头）我不同意。") == "（摇头）我不同意。");
            var forbiddenSuffix = new ActionFilterProbe(r => Select(r, [0])!.Replace("\"}", "新增事实\"}"));
            Check("动作位置以外增写被拒绝", forbiddenSuffix.Run("（点头）原有对话。") == "（点头）原有对话。");
            var droppedToken = new ActionFilterProbe(_ => "{\"actionIds\":[0],\"reply\":\"任意改写\"}");
            Check("丢失数学保留标记时回退", droppedToken.Run("（点头）2exp(i·2kπ/5)") == "（点头）2exp(i·2kπ/5)");
            var pure = new ActionFilterProbe(r => Select(r, [0]));
            Check("确认整条仅有动作时允许删空", pure.Run("（点头）") == "");
            var fullDelete = new ActionFilterProbe(r => Select(r, [0, 1]));
            Check("多个动作全部确认后允许删空", fullDelete.Run("（点头）（微笑）") == "");
            var partialDelete = new ActionFilterProbe(_ => "{\"actionIds\":[0],\"reply\":\"\"}");
            Check("空结果不能丢失未选中的候选", partialDelete.Run("（点头）（普通解释）") == "（点头）（普通解释）");
            foreach (var input in new[] { "（点头）未闭合(foo", "（点头）`未闭合代码", "（点头）\uE010C0\uE011", string.Concat(Enumerable.Repeat("（点头）", 9)), new string('文', 6001) })
            {
                var probe = new ActionFilterProbe(_ => throw new InvalidOperationException("No call expected"));
                Check("边界输入保守保留且不请求", probe.Run(input) == input && probe.Calls == 0);
            }
            var unavailable = new ActionFilterProbe(_ => null, HttpStatusCode.Unauthorized);
            Check("模型不可用保留原文", unavailable.Run("（点头）正文。") == "（点头）正文。");
            var invalidProvider = new ActionFilterProbe(_ => null, providerBody: "{broken");
            Check("模型供应商坏 JSON 保留原文", invalidProvider.Run("（点头）正文。") == "（点头）正文。");
            var emptyProvider = new ActionFilterProbe(_ => null, providerBody: "{\"choices\":[]}");
            Check("模型供应商空 choices 保留原文", emptyProvider.Run("（点头）正文。") == "（点头）正文。");
            var timeout = new ActionFilterProbe(_ => throw new OperationCanceledException());
            Check("内部取消/超时回退原文", timeout.Run("（点头）正文。") == "（点头）正文。");
            var missingKey = new ActionFilterProbe(_ => throw new InvalidOperationException("No call expected"), apiKey: "");
            Check("缺少模型凭据保留原文且无请求", missingKey.Run("（点头）正文。") == "（点头）正文。" && missingKey.Calls == 0);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            var callerCancelled = false;
            try { pure.Filter.FilterAsync("（点头）正文。", cancel.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { callerCancelled = true; }
            Check("调用方取消继续上抛", callerCancelled);

            var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var uncooperative = new ActionNarrationFilter(new FakeModelClient { ChatCompletion = _ => pending.Task }, pure.Settings);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var timedOut = uncooperative.FilterAsync("（点头）正文。").GetAwaiter().GetResult();
            pending.SetResult(null);
            Check("不响应取消的客户端仍受8秒总预算约束", timedOut == "（点头）正文。" && timer.Elapsed < TimeSpan.FromSeconds(12));
            using var duringCall = new CancellationTokenSource();
            var cancelling = new ActionNarrationFilter(new FakeModelClient
            {
                ChatCompletion = ct => { duringCall.Cancel(); return Task.FromCanceled<string?>(ct); }
            }, pure.Settings);
            callerCancelled = false;
            try { cancelling.FilterAsync("（点头）正文。", duringCall.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { callerCancelled = true; }
            Check("在途调用方取消也继续上抛", callerCancelled);

            foreach (var math in new[] { "a * beta * c", "a*beta*c", "长*宽*高", "长 * 宽 * 高", "（x ∈ R）", "*δ*", "x + y = z" })
            {
                var probe = new ActionFilterProbe(r => Select(r, [0]));
                Check("数学内容不能进入可删候选表：" + math,
                    probe.Run("（点头）" + math) == math && probe.LastInput!["candidates"]!.AsArray().Count == 1);
            }
            foreach (var code in new[] { "\n~~~~text\n~~~\n（摇头）\n~~~~", "\n````text\n```\n（摇头）\n````" })
            {
                var probe = new ActionFilterProbe(r => Select(r, [0]));
                Check("长围栏中的括号不成为候选", probe.Run("（点头）" + code) == code && probe.LastInput!["candidates"]!.AsArray().Count == 1);
            }
            foreach (var code in new[] { "（点头）示例：\n~~~text\n（摇头）", "（点头）示例：\n```text\n（摇头）", "（点头）\n~~~~text\n（摇头）\n~~~\n（晃耳朵）", "（点头）\n````text\n（摇头）\n```\n（晃耳朵）", "（点头）\n~~~text\n~~~`\n（摇头）", "（点头）\n```text\n```~\n（摇头）" })
            {
                var probe = new ActionFilterProbe(_ => throw new InvalidOperationException("No call expected"));
                Check("未闭合围栏保留全条而不判断代码", probe.Run(code) == code && probe.Calls == 0);
            }
            var urlWrapper = new ActionFilterProbe(r => Select(r, [0]));
            Check("全角URL括号不阻止真正动作复核", urlWrapper.Run("（点头）查看（https://example.com）") == "查看（https://example.com）" && urlWrapper.Calls == 1);

            foreach (var mutate in new Func<JsonObject, string>[]
            {
                r => Select(r, [99]),
                r => Select(r, [0], new string('字', 101)),
                r => Select(r, [0], "\uE010P999\uE011"),
                r => Select(r, [0], "\uE010broken"),
                r => JsonSerializer.Serialize(new { actionIds = new[] { 0 }, reply = r["body"]!.GetValue<string>() })
            })
            {
                var probe = new ActionFilterProbe(mutate);
                Check("越界/伪造/未删动作/超长槽位均回退", probe.Run("（点头）原有对话。") == "（点头）原有对话。");
            }
            foreach (var change in new Func<string, string>[]
            {
                body =>
                {
                    var markers = System.Text.RegularExpressions.Regex.Matches(body, "\uE010P[0-9]+\uE011");
                    return body.Replace(markers[0].Value, markers[1].Value, StringComparison.Ordinal);
                },
                body =>
                {
                    var markers = System.Text.RegularExpressions.Regex.Matches(body, "\uE010P[0-9]+\uE011");
                    return body.Replace(markers[0].Value, "swap-token", StringComparison.Ordinal)
                        .Replace(markers[1].Value, markers[0].Value, StringComparison.Ordinal)
                        .Replace("swap-token", markers[1].Value, StringComparison.Ordinal);
                }
            })
            {
                var probe = new ActionFilterProbe(r => JsonSerializer.Serialize(new
                {
                    actionIds = new[] { 0 }, reply = change(r["body"]!.GetValue<string>().Replace("\uE010C0\uE011", ""))
                }));
                Check("保留标记重复或重排一律回退", probe.Run("（点头）x=2 与 y=3。") == "（点头）x=2 与 y=3。");
            }
            ActionNarrationSenderTests();
        }
        finally { FileLog.WriteToFile = oldLogging; }
    }

    private static void ActionNarrationSenderTests()
    {
        var probe = new ActionFilterProbe(r => Select(r, [0]));
        probe.Settings.Apply(s => { s.FilterActionNarration = true; s.SplitReplies = false; });
        var source = new BotAgent.Services.Local.LocalChannelSource();
        var registry = new BotAgent.Services.Conversations.ConversationRegistry(new FakeConversationRepository(),
            probe.Settings, source, _ => true, _ => { });
        var ledger = new BotAgent.Services.Conversations.OwnMessageLedger(new FakeOwnMessageRepository(), _ => { });
        var sender = new PlainSender(probe.Settings, source, registry, new BotAgent.Services.Panel.PanelNotifier(),
            ledger, _ => { }, new BotAgent.Services.Ops.TurnTraceStore(), model: probe.Client);
        var report = sender.SendWithCadenceAsync(true, 10001, "（点头）结果：2exp(i·2kπ/5)", null).GetAwaiter().GetResult();
        Check("真实发送链语义过滤后数学完整到出箱", report.AnySent && report.Text == "结果：2exp(i·2kπ/5)" && source.Outbox.Count == 1 && probe.Calls == 1);
        probe.Settings.Apply(s => s.FilterActionNarration = false);
        report = sender.SendWithCadenceAsync(true, 10001, "（点头）正文。", null).GetAwaiter().GetResult();
        Check("发送开关关闭不发辅助请求", report.Text == "（点头）正文。" && probe.Calls == 1);
        probe.Settings.Apply(s => s.FilterActionNarration = true);
        var noModel = new PlainSender(probe.Settings, source, registry, new BotAgent.Services.Panel.PanelNotifier(),
            ledger, _ => { }, new BotAgent.Services.Ops.TurnTraceStore());
        report = noModel.SendWithCadenceAsync(true, 10001, "（点头）2*3*4", null).GetAwaiter().GetResult();
        Check("未装配辅助模型保留原文与公式", report.Text == "（点头）2*3*4");
        report = noModel.SendWithCadenceAsync(true, 10001, "（点头）长*宽*高", null).GetAwaiter().GetResult();
        Check("无模型路径的Markdown降级也保留CJK乘号", report.Text == "（点头）长*宽*高");
        var mathProbe = new ActionFilterProbe(r => Select(r, [0]));
        var mathSender = new PlainSender(probe.Settings, source, registry, new BotAgent.Services.Panel.PanelNotifier(),
            ledger, _ => { }, new BotAgent.Services.Ops.TurnTraceStore(), model: mathProbe.Client);
        report = mathSender.SendWithCadenceAsync(true, 10001, "（点头）长*宽*高", null).GetAwaiter().GetResult();
        Check("真实语义发送路径保留CJK变量与乘号", report.Text == "长*宽*高" && mathProbe.Calls == 1 && mathProbe.LastInput!["candidates"]!.AsArray().Count == 1);
        var unsafeRewrite = new ActionFilterProbe(r => Select(r, [0], "/opt/qqchat/data/example.txt"));
        var unsafeSender = new PlainSender(probe.Settings, source, registry, new BotAgent.Services.Panel.PanelNotifier(),
            ledger, _ => { }, new BotAgent.Services.Ops.TurnTraceStore(), model: unsafeRewrite.Client);
        report = unsafeSender.SendWithCadenceAsync(true, 10001, "（点头）正文。", null).GetAwaiter().GetResult();
        Check("改写新增DLP风险退回已审计原文", report.Text == "（点头）正文。");
        var blocked = sender.SendWithCadenceAsync(true, 10001, "（点头）/opt/qqchat/data/example.txt", null).GetAwaiter().GetResult();
        Check("原回复DLP拦截仍在辅助请求之前", !blocked.AnySent && probe.Calls == 1);
    }

    private static string Select(JsonObject request, int[] ids, string replacement = "")
    {
        var body = request["body"]!.GetValue<string>();
        foreach (var id in ids) body = body.Replace($"\uE010C{id}\uE011", replacement, StringComparison.Ordinal);
        return JsonSerializer.Serialize(new { actionIds = ids, reply = body });
    }

    private sealed class ActionFilterProbe
    {
        public int Calls { get; private set; }
        public JsonObject? LastInput { get; private set; }
        public ActionNarrationFilter Filter { get; }
        public SettingsBox Settings { get; }
        public OpenAiClient Client { get; }

        public ActionFilterProbe(Func<JsonObject, string?> respond, HttpStatusCode status = HttpStatusCode.OK,
            string? providerBody = null, string apiKey = "synthetic-key")
        {
            Settings = new SettingsBox(new AppSettings { ApiKey = apiKey, Model = "synthetic-model", ModelBaseUrl = "https://example.com/v1" });
            var fetcher = new FakeHttpFetcher
            {
                OnSend = async (request, ct) =>
                {
                    Calls++;
                    var payload = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
                    LastInput = JsonNode.Parse(payload["messages"]!.AsArray()[1]!["content"]!.GetValue<string>())!.AsObject();
                    return new HttpResponseMessage(status)
                    {
                        Content = new StringContent(providerBody ?? JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { message = new { content = respond(LastInput) } } }
                        }))
                    };
                }
            };
            Client = new OpenAiClient(Settings, fetcher, new FakeHttpFetcher(), new FakeImageDownloader(), new FakeModelTransport());
            Filter = new ActionNarrationFilter(Client, Settings);
        }

        public string Run(string text) => Filter.FilterAsync(text).GetAwaiter().GetResult();
    }
}
