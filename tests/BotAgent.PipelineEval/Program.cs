using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Services.Agent;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Rendering;
using BotAgent.Domain.Reply;
using BotAgent.Services.Participation;

namespace BotAgent.PipelineEval;

/// <summary>
/// V3 §11 的**隔离评测**：读 <c>tests/fixtures/pipeline/*.json</c> 的三类合成样例，
/// 喂给真实组件，统计 §11.3 的指标，产出 JSON + CSV + Markdown。
///
/// 诚实口径（必须一直保持）：
///   · 这是**确定性管线回归**（假模型 + 固定输入 + 注入时间），**不是模型智能评分**；
///   · 失败 / 超时 / 取消 / 部分发送都计入分母；分母为零记 <c>N/A</c>；
///   · 真模型评测**未运行** —— 需要真实密钥与费用授权，本工程绝不会去连真模型。
/// </summary>
public static class Program
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 12, 0, 0, TimeSpan.FromHours(9));

    public static int Main(string[] args)
    {
        var repoRoot = FindRepoRoot();
        var fixtureDir = Path.Combine(repoRoot, "tests", "fixtures", "pipeline");

        // 隔离证据：另起一个临时目录，跑完之后它必须是空的（纯逻辑评测不该落任何文件）
        var tempDir = Path.Combine(Path.GetTempPath(), "qqchat-eval-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        // 把**运行数据目录**也指到这个临时目录，并关掉文件日志：
        // 判定链在畸形输出时会调 FileLog，不设这两个开关就会往仓库 runtime/（或环境里已有的
        // QQCHAT_DATA_DIR —— 那可能就是线上目录）追加 WARN 行，隔离声明就成了假的（V3 §11.1）。
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", tempDir);
        BotAgent.Services.FileLog.WriteToFile = false;

        Console.WriteLine("Bot Agent · 隔离评测（确定性管线回归）");
        Console.WriteLine("仓库根目录: " + repoRoot);
        Console.WriteLine("夹具目录  : " + fixtureDir);
        Console.WriteLine("临时目录  : " + tempDir);
        Console.WriteLine(new string('─', 74));

        if (!Directory.Exists(fixtureDir))
        {
            // 夹具不存在 = 外部替身不可用 → **失败关闭**（不是跳过、不是当成 0 例通过）
            Console.WriteLine("✗ 找不到夹具目录（外部替身不可用）→ 按 Fail-Closed 直接失败");
            return 2;
        }

        // 批次 6 起解析器是 **domain 的公开纯函数**（Domain/Reply/ModelOutputParser）：不再反射穿私有方法。
        // 断言意图不变：driver=parser 的样例会走**真实解析链路**，不是只测纯规则类。
        if (typeof(ModelOutputParser).Namespace != "BotAgent.Domain.Reply")
        {
            Console.WriteLine("✗ 解析器不在 domain（解析链路搬家了？）→ 失败关闭");
            return 2;
        }

        var cases = new List<CaseResult>();
        foreach (var file in new[] { "on-demand.json", "research.json", "social.json" })
        {
            var path = Path.Combine(fixtureDir, file);
            if (!File.Exists(path))
            {
                Console.WriteLine($"✗ 缺少夹具文件 {file} → 失败关闭");
                return 2;
            }

            var doc = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                      ?? throw new InvalidOperationException($"{file} 不是合法的 JSON 对象");
            var category = doc["category"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file);
            Console.WriteLine();
            Console.WriteLine($"▶ {category} · {doc["title"]?.GetValue<string>()}");

            foreach (var node in doc["cases"]?.AsArray() ?? new JsonArray())
            {
                var c = node as JsonObject ?? throw new InvalidOperationException("样例不是 JSON 对象");
                var result = RunCase(category, c);
                cases.Add(result);
                Console.WriteLine("  " + (result.Passed ? "✅ " : "✗  ") + result.Id + " · " + result.Title
                                  + (result.Passed ? string.Empty : "  → " + result.Detail));
            }
        }

        var metrics = Metrics.Compute(cases);
        var isolation = new (string Name, string Value)[]
        {
            ("临时目录（= 本次运行数据目录）里新建的文件数", Directory.GetFileSystemEntries(tempDir).Length.ToString(CultureInfo.InvariantCulture)),
            ("是否使用真实密钥", "否（本工程不读任何密钥）"),
            ("是否连网", "否"),
            ("是否写运行数据目录", "否（QQCHAT_DATA_DIR 指向临时目录，且文件日志已关闭）"),
            ("唯一落盘物", "docs/engineering/eval-results/{results.json,results.csv,summary.md}"),
        };

        var outputDir = Path.Combine(repoRoot, "docs", "engineering", "eval-results");
        Directory.CreateDirectory(outputDir);
        WriteJson(Path.Combine(outputDir, "results.json"), cases, metrics, isolation);
        WriteCsv(Path.Combine(outputDir, "results.csv"), cases);
        WriteSummary(Path.Combine(outputDir, "summary.md"), cases, metrics, isolation);

        Console.WriteLine();
        Console.WriteLine(new string('─', 74));
        Console.WriteLine("指标（分母为 0 记 N/A）：");
        foreach (var m in metrics)
        {
            Console.WriteLine($"  {m.Name,-14} {m.Display}");
        }

        Console.WriteLine();
        Console.WriteLine($"样例合计 {cases.Count}：通过 {cases.Count(c => c.Passed)}，失败 {cases.Count(c => !c.Passed)}");
        Console.WriteLine("产物：docs/engineering/eval-results/{results.json,results.csv,summary.md}");

        return cases.All(c => c.Passed) ? 0 : 1;
    }

    // ───────────────────────────── 单条样例 ─────────────────────────────

    private static CaseResult RunCase(string category, JsonObject c)
    {
        var id = c["id"]?.GetValue<string>() ?? "(无 id)";
        var title = c["title"]?.GetValue<string>() ?? string.Empty;
        var mechanism = c["mechanism"]?.GetValue<string>() ?? string.Empty;
        var fault = c["fault"]?.GetValue<bool>() ?? false;
        var expect = c["expect"] as JsonObject ?? new JsonObject();

        // 先解析“声明位”：万一这条样例在下面抛异常，兜底结果也要带上它们 ——
        // 否则该样例会从指标分母里整体消失（分母是靠 ExpectSend/Allow/Execute 挑的，V3 §11.3）。
        static bool? Declared(JsonObject o, string key)
            => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

        var declaredSend = Declared(expect, "send");
        var declaredAllow = Declared(expect, "allow");
        var declaredExecute = Declared(expect, "execute");

        try
        {
            return mechanism switch
            {
                "decision" => RunDecision(category, id, title, mechanism, fault, c, expect),
                "gate" => RunGate(category, id, title, mechanism, fault, c, expect),
                "approval" => RunApproval(category, id, title, mechanism, fault, c, expect),
                "participation" => RunParticipation(category, id, title, mechanism, fault, c, expect),
                "replyTarget" => RunReplyTarget(category, id, title, mechanism, fault, c, expect),
                "plaintext" => RunPlainText(category, id, title, mechanism, fault, c, expect),
                _ => new CaseResult(category, id, title, mechanism, fault, false, "未知 mechanism: " + mechanism),
            };
        }
        catch (Exception ex)
        {
            return new CaseResult(category, id, title, mechanism, fault, false, "样例执行异常: " + ex.Message,
                ExpectSend: declaredSend, ExpectAllow: declaredAllow, ExpectExecute: declaredExecute);
        }
    }

    private static CaseResult RunDecision(
        string category, string id, string title, string mechanism, bool fault,
        JsonObject c, JsonObject expect)
    {
        var driver = c["driver"]?.GetValue<string>() ?? "parser";
        // 先取“期望发不发”：下面两条新的断言要在分支里就带上它（异常/泄漏的样例必须留在分母里）
        var declaredSend = expect["send"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) ? sb : (bool?)null;
        DecisionVerdict verdict;
        string? content;
        ReplyAction action;
        string? reason;
        bool malformed;
        string? toolId;

        if (driver == "parser")
        {
            var raw = c["modelOutput"]?.GetValue<string>() ?? string.Empty;
            // 第二个参数是「允许提问」开关：夹具默认按**关**跑（与线上默认一致）
            var parsed = ModelOutputParser.Parse(raw, c["questionsEnabled"]?.GetValue<bool>() ?? false).Result;
            content = parsed.Reply;
            action = parsed.Action;
            reason = parsed.ReasonCode;
            malformed = parsed.Malformed;
            toolId = parsed.ToolId;

            // 副作用字段（V3 §8.1）：显式 silent / ask / tool / 未知动作时，模型给的
            // speak / shareSong / search… 一个都不许带出去 —— 否则“说了不说”却还发语音、分享歌曲。
            if (expect["noSideEffects"]?.GetValue<bool>() == true)
            {
                var leaked = new List<string>();
                if (parsed.StickerId is not null) leaked.Add("sticker");
                if (parsed.PokeTargetId is not null) leaked.Add("poke");
                if (parsed.Speak is not null) leaked.Add("speak");
                if (parsed.Listen is not null) leaked.Add("listen");
                if (parsed.ShareSong is not null) leaked.Add("shareSong");
                if (parsed.Search is not null) leaked.Add("search");
                if (parsed.Read is not null) leaked.Add("read");
                if (parsed.ReplyToMessageId is not null) leaked.Add("replyTo");
                if (parsed.Both) leaked.Add("both");
                if (leaked.Count > 0)
                {
                    return new CaseResult(category, id, title, mechanism, fault, false,
                        "副作用字段泄漏: " + string.Join(",", leaked), ExpectSend: declaredSend);
                }
            }

            // 兼容红线：旧协议（没有 action 字段）/ action=reply 的“只发语音”必须原样保留
            if (expect["keepsSpeak"]?.GetValue<bool>() == true && parsed.Speak is null)
            {
                return new CaseResult(category, id, title, mechanism, fault, false,
                    "应保留 speak（只发语音那条兼容路），却被清掉了", ExpectSend: declaredSend);
            }
        }
        else
        {
            var d = c["direct"] as JsonObject ?? new JsonObject();
            var v = ReplyDecisionRules.Decide(
                d["action"]?.GetValue<string>(),
                d["reply"]?.GetValue<string>(),
                d["reasonCode"]?.GetValue<string>(),
                d["upstreamEmpty"]?.GetValue<bool>() ?? false);
            verdict = v;
            content = v.Content;
            action = v.Action;
            reason = v.ReasonCode;
            malformed = v.Malformed;
            toolId = v.ToolId;
        }

        var send = content is not null;
        var detail = $"send={send} action={action} reason={reason ?? "(null)"} malformed={malformed}";

        if (expect["send"] is JsonValue es && send != es.GetValue<bool>())
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectSend: declaredSend);
        }

        if (expect["replyNull"]?.GetValue<bool>() == true && content is not null)
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail + "（要求正文为 null）",
                ExpectSend: declaredSend);
        }

        if (expect["reason"] is JsonValue er &&
            !string.Equals(reason, er.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectSend: declaredSend);
        }

        if (expect["malformed"] is JsonValue em && malformed != em.GetValue<bool>())
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectSend: declaredSend);
        }

        if (expect["toolId"] is JsonValue et &&
            !string.Equals(toolId, et.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false,
                detail + $" tool={toolId ?? "(null)"}", ExpectSend: declaredSend);
        }

        return new CaseResult(category, id, title, mechanism, fault, true, detail, ExpectSend: declaredSend);
    }

    private static CaseResult RunGate(
        string category, string id, string title, string mechanism, bool fault, JsonObject c, JsonObject expect)
    {
        var g = c["gate"] as JsonObject ?? throw new InvalidOperationException("gate 节缺失");
        var sw = g["switches"] as JsonObject ?? new JsonObject();
        var set = ChatCapabilitySet.FromSwitches(
            enableWebSearch: sw["webSearch"]?.GetValue<bool>() ?? true,
            enableMusic: sw["music"]?.GetValue<bool>() ?? true,
            enableVoice: sw["voice"]?.GetValue<bool>() ?? true,
            enableStickers: sw["stickers"]?.GetValue<bool>() ?? true,
            enablePoke: sw["poke"]?.GetValue<bool>() ?? true,
            scenario: g["scenario"]?.GetValue<string>() ?? string.Empty,
            approvalsEnabled: g["approvalsEnabled"]?.GetValue<bool>() ?? false,
            questionsEnabled: g["questionsEnabled"]?.GetValue<bool>() ?? false);

        ApprovalTicket? ticket = null;
        if (g["ticket"] is JsonObject t)
        {
            ticket = new ApprovalTicket(
                t["requestId"]?.GetValue<string>() ?? "TICKET",
                t["toolId"]?.GetValue<string>() ?? string.Empty,
                t["conversationKey"]?.GetValue<string>() ?? string.Empty,
                T0);
        }

        var decision = set.Check(
            g["tool"]?.GetValue<string>() ?? string.Empty,
            g["conversation"]?.GetValue<string>() ?? "group:10001",
            callIndex: g["callIndex"]?.GetValue<int>() ?? 0,
            targetConversationKey: g["targetConversation"]?.GetValue<string>(),
            untrustedHint: g["untrustedHint"]?.GetValue<string>(),
            ticket: ticket);

        var detail = decision.Describe();
        var declaredAllow = expect["allow"] is JsonValue dv ? dv.GetValue<bool>() : (bool?)null;
        if (expect["allow"] is JsonValue ea && decision.Allow != ea.GetValue<bool>())
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectAllow: declaredAllow);
        }

        if (expect["reason"] is JsonValue er &&
            !string.Equals(decision.ReasonCode, er.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectAllow: declaredAllow);
        }

        return new CaseResult(category, id, title, mechanism, fault, true, detail, ExpectAllow: declaredAllow);
    }

    private static CaseResult RunApproval(
        string category, string id, string title, string mechanism, bool fault, JsonObject c, JsonObject expect)
    {
        var a = c["approval"] as JsonObject ?? throw new InvalidOperationException("approval 节缺失");
        var create = a["create"] as JsonObject ?? new JsonObject();

        var store = new ApprovalStore();
        var created = ApprovalFlow.CreateForModelTool(
            store,
            requestedToolId: create["tool"]?.GetValue<string>(),
            conversationKey: create["conversation"]?.GetValue<string>() ?? "group:10001",
            requesterFingerprint: "msg:1",
            configuredApprovers: (create["approvers"] as JsonArray)?.Select(x => x!.GetValue<string>()) ?? Array.Empty<string>(),
            allowGroupAdmins: create["allowGroupAdmins"]?.GetValue<bool>() ?? true,
            isGroup: create["isGroup"]?.GetValue<bool>() ?? true,
            now: T0,
            newRequestId: () => create["requestId"]?.GetValue<string>() ?? "FIXTURE",
            policyVersion: create["policyVersion"]?.GetValue<int>() ?? 1);

        var detail = $"created={created.Created} reason={created.ReasonCode}";
        var declaredExecute = expect["execute"] is JsonValue dve ? dve.GetValue<bool>() : (bool?)null;
        if (expect["created"] is JsonValue ec && created.Created != ec.GetValue<bool>())
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectExecute: declaredExecute);
        }

        if (expect["reason"] is JsonValue er0 && !created.Created &&
            !string.Equals(created.ReasonCode, er0.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectExecute: declaredExecute);
        }

        var act = a["act"] as JsonObject;
        if (act is null)
        {
            // 只验“开不开单”的样例（例如模型点名了没登记的工具）
            var okOnlyCreated = expect["reason"] is JsonValue er &&
                                string.Equals(created.ReasonCode, er.GetValue<string>(), StringComparison.Ordinal);
            return new CaseResult(category, id, title, mechanism, fault, okOnlyCreated, detail,
                ExpectExecute: declaredExecute);
        }

        var command = ApprovalFlow.TryParseCommand(act["command"]?.GetValue<string>());
        if (command is null)
        {
            // 解析不出来 = 不算命令：只要夹具声明了 notACommand，就算通过（消息会走普通链路）
            var isExpected = expect["notACommand"]?.GetValue<bool>() == true;
            return new CaseResult(category, id, title, mechanism, fault, isExpected,
                detail + " command=null（不解析）", ExpectExecute: declaredExecute);
        }

        var outcome = ApprovalFlow.Handle(
            store,
            command.Value,
            requesterId: act["actor"]?.GetValue<string>() ?? "user:10001",
            requesterRole: act["role"]?.GetValue<string>(),
            conversationKey: act["conversation"]?.GetValue<string>() ?? "group:10001",
            now: T0.AddSeconds(act["atSeconds"]?.GetValue<double>() ?? 0),
            currentPolicyVersion: act["currentPolicyVersion"]?.GetValue<int>() ?? 1);

        detail += $" execute={outcome.ShouldExecute} reason={outcome.ReasonCode}";
        if (expect["execute"] is JsonValue ee && outcome.ShouldExecute != ee.GetValue<bool>())
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectExecute: declaredExecute);
        }

        if (expect["reason"] is JsonValue er2 &&
            !string.Equals(outcome.ReasonCode, er2.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail, ExpectExecute: declaredExecute);
        }

        if (a["replay"] is JsonObject replay)
        {
            var replayCommand = ApprovalFlow.TryParseCommand(replay["command"]?.GetValue<string>());
            if (replayCommand is null)
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail + " replay 命令不解析");
            }

            var replayOutcome = ApprovalFlow.Handle(
                store,
                replayCommand.Value,
                requesterId: replay["actor"]?.GetValue<string>() ?? "user:10001",
                requesterRole: replay["role"]?.GetValue<string>(),
                conversationKey: replay["conversation"]?.GetValue<string>() ?? "group:10001",
                now: T0.AddSeconds(replay["atSeconds"]?.GetValue<double>() ?? 6),
                currentPolicyVersion: replay["currentPolicyVersion"]?.GetValue<int>() ?? 1);

            detail += $" | replay execute={replayOutcome.ShouldExecute} reason={replayOutcome.ReasonCode}";
            if (expect["replayExecute"] is JsonValue re && replayOutcome.ShouldExecute != re.GetValue<bool>())
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail,
                    ExpectExecute: declaredExecute);
            }

            if (expect["replayReason"] is JsonValue rr &&
                !string.Equals(replayOutcome.ReasonCode, rr.GetValue<string>(), StringComparison.Ordinal))
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail,
                    ExpectExecute: declaredExecute);
            }
        }

        return new CaseResult(category, id, title, mechanism, fault, true, detail, ExpectExecute: declaredExecute);
    }

    private static CaseResult RunParticipation(
        string category, string id, string title, string mechanism, bool fault, JsonObject c, JsonObject expect)
    {
        var machine = new ParticipationStateMachine(new ParticipationPolicy());
        var registry = new ParticipationRegistry(new ParticipationPolicy());
        var key = "group:10001";
        var tracked = registry.For(key);

        foreach (var stepNode in c["script"]?.AsArray() ?? new JsonArray())
        {
            var step = stepNode as JsonObject ?? throw new InvalidOperationException("script 步骤不是对象");
            var evtName = step["event"]?.GetValue<string>() ?? string.Empty;
            if (!Enum.TryParse<ParticipationEvent>(evtName, ignoreCase: true, out var evt))
            {
                return new CaseResult(category, id, title, mechanism, fault, false, "未知事件名: " + evtName);
            }

            var now = T0.AddSeconds(step["atSeconds"]?.GetValue<double>() ?? 0);
            var decision = tracked.OnEvent(evt, now);

            if (step["allow"] is JsonValue sa && decision.Allow != sa.GetValue<bool>())
            {
                return new CaseResult(category, id, title, mechanism, fault, false,
                    $"{evtName} allow={decision.Allow}（期望 {sa.GetValue<bool>()}） state={decision.State} reason={decision.ReasonCode}");
            }

            if (step["state"] is JsonValue ss &&
                !string.Equals(decision.State.ToString(), ss.GetValue<string>(), StringComparison.Ordinal))
            {
                return new CaseResult(category, id, title, mechanism, fault, false,
                    $"{evtName} state={decision.State}（期望 {ss.GetValue<string>()}）");
            }

            if (step["reason"] is JsonValue sr &&
                !string.Equals(decision.ReasonCode, sr.GetValue<string>(), StringComparison.Ordinal))
            {
                return new CaseResult(category, id, title, mechanism, fault, false,
                    $"{evtName} reason={decision.ReasonCode}（期望 {sr.GetValue<string>()}）");
            }
        }

        var finalState = tracked.State.ToString();
        var detail = $"finalState={finalState}";
        if (expect["finalState"] is JsonValue ef &&
            !string.Equals(finalState, ef.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail);
        }

        if (expect["isolationKey"] is JsonValue ik)
        {
            var otherKey = ik.GetValue<string>() ?? "group:20002";
            var other = registry.For(otherKey);
            detail += $" | {otherKey} state={other.State}";
            if (expect["isolationState"] is JsonValue isv &&
                !string.Equals(other.State.ToString(), isv.GetValue<string>(), StringComparison.Ordinal))
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail);
            }
        }

        _ = machine;   // 保留一个独立实例：证明状态是以台账为单位的（上面的 tracked 才有意义）
        return new CaseResult(category, id, title, mechanism, fault, true, detail);
    }

    private static CaseResult RunReplyTarget(
        string category, string id, string title, string mechanism, bool fault, JsonObject c, JsonObject expect)
    {
        var f = c["facts"] as JsonObject ?? throw new InvalidOperationException("facts 节缺失");
        long? ChosenLong(string name)
            => f[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

        var facts = new ReplyTargetFacts(
            Chosen: ChosenLong("chosen"),
            ChosenUsable: f["chosenUsable"]?.GetValue<bool>() ?? false,
            ChosenIndex: f["chosenIndex"]?.GetValue<int>() ?? -1,
            MessageCount: f["messageCount"]?.GetValue<int>() ?? 0,
            TriggerMessageId: ChosenLong("triggerMessageId"),
            TriggerIndex: f["triggerIndex"]?.GetValue<int>() ?? -1,
            // 夹具里不写就按“可用”算（老样例没有这个字段）；写了 false 才走“触发已撤回”那一支
            TriggerUsable: f["triggerUsable"]?.GetValue<bool>() ?? true,
            LastSelfIndex: f["lastSelfIndex"]?.GetValue<int>() ?? -1,
            DirectAddress: f["directAddress"]?.GetValue<bool>() ?? false,
            TriggerWasEcho: f["triggerWasEcho"]?.GetValue<bool>() ?? false);

        var result = ReplyTargetRules.Resolve(facts);
        var detail = $"target={result.Target?.ToString(CultureInfo.InvariantCulture) ?? "(null)"} reason={result.ReasonCode}";

        if (expect["target"] is JsonValue et)
        {
            var expected = et.GetValue<long?>();
            if (result.Target != expected)
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail);
            }
        }

        if (expect["reason"] is JsonValue er &&
            !string.Equals(result.ReasonCode, er.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail);
        }

        return new CaseResult(category, id, title, mechanism, fault, true, detail);
    }

    private static CaseResult RunPlainText(
        string category, string id, string title, string mechanism, bool fault, JsonObject c, JsonObject expect)
    {
        var input = c["input"]?.GetValue<string>() ?? string.Empty;
        var text = QqPlainText.Sanitize(input);
        var detail = "out=" + Snippet(text);

        foreach (var must in expect["mustContain"]?.AsArray() ?? new JsonArray())
        {
            var needle = must!.GetValue<string>();
            if (!text.Contains(needle, StringComparison.Ordinal))
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail + $"（缺少「{needle}」）");
            }
        }

        foreach (var mustNot in expect["mustNotContain"]?.AsArray() ?? new JsonArray())
        {
            var needle = mustNot!.GetValue<string>();
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return new CaseResult(category, id, title, mechanism, fault, false, detail + $"（不该出现「{needle}」）");
            }
        }

        if (expect["text"] is JsonValue et &&
            !string.Equals(text, et.GetValue<string>(), StringComparison.Ordinal))
        {
            return new CaseResult(category, id, title, mechanism, fault, false, detail);
        }

        return new CaseResult(category, id, title, mechanism, fault, true, detail);
    }

    // ───────────────────────────── 产物 ─────────────────────────────

    private static void WriteJson(
        string path, IReadOnlyList<CaseResult> cases, IReadOnlyList<MetricResult> metrics,
        IReadOnlyList<(string Name, string Value)> isolation)
    {
        var root = new JsonObject
        {
            ["title"] = "工程增强（V3）确定性管线回归结果",
            ["generatedAt"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            ["kind"] = "deterministic-pipeline-regression",
            ["fakeModelOnly"] = true,
            ["realModelEvaluation"] = "未运行（需要真实密钥与费用授权）",
            ["note"] = "假模型 + 固定输入 + 注入时间；这不是模型智能评分，分数不能冒充真实模型效果。",
            ["isolation"] = new JsonArray(isolation
                .Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["value"] = i.Value }).ToArray()),
            ["metrics"] = new JsonArray(metrics
                .Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Name,
                    ["numerator"] = m.Numerator,
                    ["denominator"] = m.Denominator,
                    ["value"] = m.Denominator == 0 ? null : Math.Round(m.Numerator * 1.0 / m.Denominator, 4),
                    ["display"] = m.Display,
                    ["note"] = m.Note,
                }).ToArray()),
            ["cases"] = new JsonArray(cases
                .Select(c => (JsonNode)new JsonObject
                {
                    ["category"] = c.Category,
                    ["id"] = c.Id,
                    ["title"] = c.Title,
                    ["mechanism"] = c.Mechanism,
                    ["fault"] = c.Fault,
                    ["passed"] = c.Passed,
                    ["detail"] = c.Detail,
                }).ToArray()),
        };

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    private static void WriteCsv(string path, IReadOnlyList<CaseResult> cases)
    {
        var sb = new StringBuilder();
        sb.AppendLine("category,id,mechanism,fault,passed,title,detail");
        foreach (var c in cases)
        {
            sb.AppendLine(string.Join(',',
                Csv(c.Category), Csv(c.Id), Csv(c.Mechanism), c.Fault ? "1" : "0", c.Passed ? "1" : "0",
                Csv(c.Title), Csv(c.Detail)));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void WriteSummary(
        string path, IReadOnlyList<CaseResult> cases, IReadOnlyList<MetricResult> metrics,
        IReadOnlyList<(string Name, string Value)> isolation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 工程增强（V3）隔离评测汇总");
        sb.AppendLine();
        sb.AppendLine($"> 生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine("> 口径：**确定性管线回归**（假模型 + 固定输入 + 注入时间）。" +
                      "**这不是模型智能评分**，分数不能冒充真实模型效果；真实模型评测**未运行**。");
        sb.AppendLine();
        sb.AppendLine("## 指标（分母为 0 记 N/A）");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 通过 / 分母 | 值 | 说明 |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var m in metrics)
        {
            sb.AppendLine($"| {m.Name} | {m.Numerator} / {m.Denominator} | {m.Display} | {m.Note} |");
        }

        sb.AppendLine();
        sb.AppendLine("## 隔离证据");
        sb.AppendLine();
        foreach (var (name, value) in isolation)
        {
            sb.AppendLine($"- {name}：{value}");
        }

        sb.AppendLine();
        sb.AppendLine("## 分场景统计");
        sb.AppendLine();
        sb.AppendLine("| 场景 | 样例 | 通过 | 失败 |");
        sb.AppendLine("| --- | --- | --- | --- |");
        foreach (var g in cases.GroupBy(c => c.Category))
        {
            sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(c => c.Passed)} | {g.Count(c => !c.Passed)} |");
        }

        var failed = cases.Where(c => !c.Passed).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## 失败样例（必须计入分母，不许排除）");
            sb.AppendLine();
            foreach (var f in failed)
            {
                sb.AppendLine($"- `{f.Id}` {f.Title} → {f.Detail}");
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static string Snippet(string text)
        => text.Length <= 60 ? text.Replace('\n', '⏎') : text[..60].Replace('\n', '⏎') + "…";

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "pipeline")) ||
                File.Exists(Path.Combine(dir.FullName, "BotAgent.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}

/// <summary>一条样例的执行结果。</summary>
public sealed record CaseResult(
    string Category, string Id, string Title, string Mechanism, bool Fault, bool Passed, string Detail,
    bool? ExpectSend = null, bool? ExpectAllow = null, bool? ExpectExecute = null);

/// <summary>一个指标（分子 / 分母 / 显示值）。分母为 0 时显示 N/A。</summary>
public sealed record MetricResult(int Numerator, int Denominator, string Name, string Note)
{
    public string Display => Denominator == 0
        ? "N/A"
        : (Numerator * 100.0 / Denominator).ToString("0.0", CultureInfo.InvariantCulture) + "%"
          + $"（{Numerator}/{Denominator}）";
}

/// <summary>V3 §11.3 的指标口径。每一条都写清“分子是谁、分母是谁”。</summary>
public static class Metrics
{
    public static IReadOnlyList<MetricResult> Compute(IReadOnlyList<CaseResult> cases)
    {
        var decision = cases.Where(c => c.Mechanism == "decision").ToList();

        // 分母一律取夹具里**声明的期望**（不是从执行结果反推 —— 否则期望和结果混在一起，指标会自己骗自己）
        var shouldReply = decision.Where(c => c.ExpectSend == true).ToList();
        var shouldSilence = decision.Where(c => c.ExpectSend == false).ToList();
        var unauthorized = cases.Where(c => c.Mechanism == "gate" && c.ExpectAllow == false).ToList();
        var approvalMustNotRun = cases.Where(c => c.Mechanism == "approval" && c.ExpectExecute == false).ToList();

        return new[]
        {
            Metric("应答召回率", shouldReply.Count(c => c.Passed), shouldReply.Count,
                "应回复样例中通过管线校验（会真的发出去）的比例"),
            Metric("误插话率", shouldSilence.Count(c => !c.Passed), shouldSilence.Count,
                "应静默样例中仍被放行的比例（越低越好）"),
            Metric("静默准确率", shouldSilence.Count(c => c.Passed), shouldSilence.Count,
                "应静默样例中没有任何外发内容的比例"),
            Metric("回复对象正确率", cases.Count(c => c.Mechanism == "replyTarget" && c.Passed),
                cases.Count(c => c.Mechanism == "replyTarget"),
                "引用目标选对（或正确地选择不引用）的比例"),
            Metric("状态转移正确率", cases.Count(c => c.Mechanism == "participation" && c.Passed),
                cases.Count(c => c.Mechanism == "participation"),
                "抽样脚本里每一步的 allow/state/reason 都与预期一致的比例"),
            Metric("工具拦截率", unauthorized.Count(c => c.Passed), unauthorized.Count,
                "**未授权**能力在真正执行前被拒绝的比例（分母=夹具里声明“应拒绝”的样例）"),
            Metric("审批安全率", approvalMustNotRun.Count(c => c.Passed), approvalMustNotRun.Count,
                "声明“不得执行”的审批样例（过期/伪造/重放/跨会话/换工具/版本过期）里，确实没执行的比例"),
            Metric("文本清洗回归", cases.Count(c => c.Mechanism == "plaintext" && c.Passed),
                cases.Count(c => c.Mechanism == "plaintext"),
                "关键正文与 URL 保留、Markdown 标记去掉的比例"),
            Metric("异常恢复", cases.Count(c => c.Fault && c.Passed), cases.Count(c => c.Fault),
                "注入故障（工具失败 / 模型超时）后仍到达预期安全终态的比例"),
            Metric("额外：合法批准确实执行", cases.Count(c => c.Mechanism == "approval" && c.ExpectExecute == true && c.Passed),
                cases.Count(c => c.Mechanism == "approval" && c.ExpectExecute == true),
                "对照项：审批安全率只证明“没乱执行”，这一条证明“该执行时真的执行了”"),
            new MetricResult(0, 0, "延迟与 Token",
                "确定性评测不产生真实延迟与 token 消耗（假模型不调用上游）；真实模型评测未运行，故记 N/A"),
        };
    }

    private static MetricResult Metric(string name, int numerator, int denominator, string note)
        => new(numerator, denominator, name, note);
}
