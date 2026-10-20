using System.Reflection;
using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// S55：ZBA 事件序号跨宿主重启。重启后序号从持久化高水位继续，旧游标要么继续收到后续事件，
/// 要么在恢复不足/超前时明确 resync，不能把新事件静默跳过。
/// </summary>
public static partial class Program
{
    private static async Task RunZbaEventRestartScenarioAsync()
    {
        Section("S55 ZBA 事件序号跨重启（旧游标 / 高水位 / resync）");

        const string adminToken = "synthetic-zba-restart-admin";
        const string readToken = "synthetic-zba-restart-read";
        var dataDir = NewDataDir("s55");
        var panel1 = FreePort(18125);
        var ws1 = FreePort(13075);
        var panel2 = FreePort(18126);
        var ws2 = FreePort(13076);
        var panel3 = FreePort(18127);
        var ws3 = FreePort(13077);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        Dictionary<string, string> Env(int panelPort, int wsPort, string? dataDirOverride = null) => new()
        {
            ["QQCHAT_DATA_DIR"] = dataDirOverride ?? dataDir,
            ["QQCHAT_API_KEY"] = "sk-mock-zba-restart",
            ["QQCHAT_BASE_URL"] = "http://127.0.0.1:1/v1",
            ["QQCHAT_MODEL"] = "mock-model",
            ["QQCHAT_ONEBOT_PROTOCOL"] = "ReverseWebSocket",
            ["QQCHAT_ONEBOT_URL"] = $"http://0.0.0.0:{wsPort}",
            ["QQCHAT_UIN"] = "10001",
            ["QQCHAT_WHITELIST"] = "99999",
            ["QQCHAT_GROUP_COOLDOWN"] = "0",
            ["QQCHAT_IDLE_FALLBACK"] = "0",
            ["QQCHAT_HEALTH_PORT"] = panelPort.ToString(),
            ["QQCHAT_PANEL_TOKEN"] = SyntheticPanelToken,
            ["BOTAGENT_ZBA_TOKEN"] = adminToken,
            ["BOTAGENT_ZBA_READ_TOKEN"] = readToken
        };

        async Task<(int Status, string Body)> SaveConfigAsync(string root, int cooldown)
        {
            var read = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, """{"kind":0}""");
            var config = JsonNode.Parse(read.Body) as JsonObject;
            var revision = config?["contractRevision"]?.GetValue<string>() ?? "cfg-0.3.3";
            var version = config?["version"]?.GetValue<string>()
                          ?? throw new InvalidOperationException("S55 configuration version missing");
            var generation = config?["generation"]?.GetValue<string>()
                             ?? throw new InvalidOperationException("S55 configuration generation missing");
            var key = $"cfg1:{generation}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:{Guid.NewGuid():N}";
            return await ZbaAsync(http, "POST", root + "/configuration/save", adminToken,
                "{\"contractRevision\":\"" + revision + "\",\"expectedVersion\":\"" + version
                + "\",\"key\":\"" + key + "\",\"changes\":{\"behavior\":{\"groupCooldownSeconds\":"
                + "{\"kind\":1,\"value\":" + cooldown + "}}},\"secrets\":[]}");
        }

        long cursor;        using (var bot = StartBot(Env(panel1, ws1)))
        {
            await WaitForPortAsync(ws1, cts.Token, bot);
            await WaitForPortAsync(panel1, cts.Token, bot);
            var root = $"http://127.0.0.1:{panel1}/zba/v1";

            var first = await SaveConfigAsync(root, 1);
            Check("S55 重启前第一次配置保存成功",
                first.Status == 200 && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                first.Status + " " + first.Body);
            var second = await SaveConfigAsync(root, 0);
            Check("S55 重启前第二次配置保存成功",
                second.Status == 200 && (JsonNode.Parse(second.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                second.Status + " " + second.Body);

            var events = await ZbaAsync(http, "GET", root + "/events?after=0&limit=200", readToken);
            var page = JsonNode.Parse(events.Body) as JsonObject;
            cursor = page?["nextSequence"]?.GetValue<long>() ?? -1;
            Check("S55 重启前已产生两个事件并取得游标", cursor >= 2, events.Status + " cursor=" + cursor);
            await bot.StopAsync();
        }

        Check("S55 重启前游标有效", cursor >= 2, "cursor=" + cursor);

        using (var bot = StartBot(Env(panel2, ws2)))
        {
            await WaitForPortAsync(ws2, cts.Token, bot);
            await WaitForPortAsync(panel2, cts.Token, bot);
            var root = $"http://127.0.0.1:{panel2}/zba/v1";

            var atCursor = await ZbaAsync(http, "GET", root + "/events?after=" + cursor, readToken);
            var atNode = JsonNode.Parse(atCursor.Body) as JsonObject;
            Check("S55 重启后旧游标==高水位：空页但启动屏障要求 resync",
                atCursor.Status == 200
                && atNode?["items"] is JsonArray { Count: 0 }
                && atNode?["resyncRequired"]?.GetValue<bool>() == true
                && atNode?["nextSequence"]?.GetValue<long>() == cursor + 1,
                atCursor.Status + " " + atCursor.Body);

            var behind = await ZbaAsync(http, "GET", root + "/events?after=" + (cursor - 1), readToken);
            var behindNode = JsonNode.Parse(behind.Body) as JsonObject;
            Check("S55 重启后空环旧游标落后高水位：要求 resync",
                behind.Status == 200
                && behindNode?["items"] is JsonArray { Count: 0 }
                && behindNode?["resyncRequired"]?.GetValue<bool>() == true,
                behind.Status + " " + behind.Body);

            var zero = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var zeroNode = JsonNode.Parse(zero.Body) as JsonObject;
            Check("S55 重启后空环零游标：要求 resync",
                zero.Status == 200
                && zeroNode?["items"] is JsonArray { Count: 0 }
                && zeroNode?["resyncRequired"]?.GetValue<bool>() == true,
                zero.Status + " " + zero.Body);

            // 屏障不是全局状态：第二个客户端用同一旧游标也要 resync，并能独立采用同一预留序号。
            var second = await ZbaAsync(http, "GET", root + "/events?after=" + cursor, readToken);
            var secondNode = JsonNode.Parse(second.Body) as JsonObject;
            Check("S55 第二个客户端同样要求 resync 并拿到同一屏障游标",
                second.Status == 200
                && secondNode?["items"] is JsonArray { Count: 0 }
                && secondNode?["resyncRequired"]?.GetValue<bool>() == true
                && secondNode?["nextSequence"]?.GetValue<long>() == cursor + 1,
                second.Status + " " + second.Body);

            var secondAdopted = await ZbaAsync(http, "GET", root + "/events?after=" + (cursor + 1), readToken);
            var secondAdoptedNode = JsonNode.Parse(secondAdopted.Body) as JsonObject;
            Check("S55 第二个客户端独立完成屏障，不依赖第一个客户端",
                secondAdopted.Status == 200
                && secondAdoptedNode?["items"] is JsonArray { Count: 0 }
                && secondAdoptedNode?["resyncRequired"]?.GetValue<bool>() == false,
                secondAdopted.Status + " " + secondAdopted.Body);

            var save = await SaveConfigAsync(root, 1);
            Check("S55 重启后配置保存成功",
                save.Status == 200 && (JsonNode.Parse(save.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                save.Status + " " + save.Body);

            var afterSave = await ZbaAsync(http, "GET", root + "/events?after=" + cursor + "&limit=200", readToken);
            var afterNode = JsonNode.Parse(afterSave.Body) as JsonObject;
            var items = afterNode?["items"] as JsonArray;
            Check("S55 重启后旧游标收到屏障后的新事件，且仍要求 resync",
                afterSave.Status == 200
                && items is { Count: > 0 }
                && items.Any(item => item?["sequence"]?.GetValue<long>() == cursor + 2)
                && afterNode?["resyncRequired"]?.GetValue<bool>() == true,
                afterSave.Status + " " + afterSave.Body);

            // 按文档“按 nextSequence 追”：采用屏障游标后即可完成一次重读，不再 resync。
            var adopted = await ZbaAsync(http, "GET", root + "/events?after=" + (cursor + 1), readToken);
            var adoptedNode = JsonNode.Parse(adopted.Body) as JsonObject;
            var adoptedItems = (adoptedNode?["items"] as JsonArray)?
                .Select(item => item?["sequence"]?.GetValue<long>() ?? -1).ToArray() ?? Array.Empty<long>();
            Check("S55 采用屏障游标后完成一次 resync，新事件不丢",
                adopted.Status == 200
                && adoptedItems.SequenceEqual(new[] { cursor + 2 })
                && adoptedNode?["resyncRequired"]?.GetValue<bool>() == false,
                adopted.Status + " sequences=" + string.Join(",", adoptedItems) + " " + adopted.Body);

            var forward = await ZbaAsync(http, "GET", root + "/events?after=" + (cursor + 50), readToken);
            Check("S55 超前游标要求 resync",
                forward.Status == 200
                && (JsonNode.Parse(forward.Body) as JsonObject)?["resyncRequired"]?.GetValue<bool>() == true,
                forward.Status + " " + forward.Body);

            var missed = await ZbaAsync(http, "GET", root + "/events?after=" + (cursor - 1), readToken);
            Check("S55 旧游标小于高水位且旧事件已不在环内：要求 resync",
                missed.Status == 200
                && (JsonNode.Parse(missed.Body) as JsonObject)?["resyncRequired"]?.GetValue<bool>() == true,
                missed.Status + " " + missed.Body);

            await bot.StopAsync();
        }

        // 损坏的序号文件必须记录并开新代际，不能静默回到 0。
        var sequenceFile = Path.Combine(dataDir, "data", "zba-event-sequence");
        Directory.CreateDirectory(Path.GetDirectoryName(sequenceFile)!);
        await File.WriteAllTextAsync(sequenceFile, "not-a-number", cts.Token);

        using (var bot = StartBot(Env(panel3, ws3)))
        {
            await WaitForPortAsync(ws3, cts.Token, bot);
            await WaitForPortAsync(panel3, cts.Token, bot);
            var root = $"http://127.0.0.1:{panel3}/zba/v1";

            var corrupted = await ZbaAsync(http, "GET", root + "/events?after=" + (cursor + 1), readToken);
            var corruptedNode = JsonNode.Parse(corrupted.Body) as JsonObject;
            Check("S55 序号文件损坏后旧游标要求 resync",
                corrupted.Status == 200
                && corruptedNode?["items"] is JsonArray { Count: 0 }
                && corruptedNode?["resyncRequired"]?.GetValue<bool>() == true,
                corrupted.Status + " " + corrupted.Body);

            var save = await SaveConfigAsync(root, 0);
            Check("S55 序号文件损坏后仍可保存",
                save.Status == 200 && (JsonNode.Parse(save.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                save.Status + " " + save.Body);

            var persisted = long.TryParse(
                (await File.ReadAllTextAsync(sequenceFile, cts.Token)).Trim(), out var sequence)
                ? sequence
                : -1;
            Check("S55 序号文件损坏后使用新代际而非 0",
                persisted >= 1_000_000_000_000,
                "persisted=" + persisted);
        }

        var corruptedLogPath = Path.Combine(dataDir, "logs", "qqchat.log");
        var corruptedLogged = File.Exists(corruptedLogPath)
                              && File.ReadLines(corruptedLogPath)
                                  .Any(line => line.Contains("事件序号文件内容无效", StringComparison.Ordinal));
        Check("S55 序号文件损坏会写安全日志", corruptedLogged, corruptedLogPath);

        // 序号写入失败（W5-G1-IO）：不能把未持久化的序号确认给客户端，否则重启后重用序号会让旧游标静默漏事件。
        var faultDir = NewDataDir("s55-seq-fault");
        var faultPanelBefore = FreePort(18128);
        var faultWsBefore = FreePort(13078);
        var faultPanel = FreePort(18129);
        var faultWs = FreePort(13079);
        var faultPanelAfter = FreePort(18130);
        var faultWsAfter = FreePort(13080);
        var faultSequenceFile = Path.Combine(faultDir, "data", "zba-event-sequence");
        long faultCursor;

        using (var bot = StartBot(Env(faultPanelBefore, faultWsBefore, faultDir)))
        {
            await WaitForPortAsync(faultWsBefore, cts.Token, bot);
            await WaitForPortAsync(faultPanelBefore, cts.Token, bot);
            var root = $"http://127.0.0.1:{faultPanelBefore}/zba/v1";
            var baseline = await SaveConfigAsync(root, 1);
            Check("S55 写失败前基线配置保存成功",
                baseline.Status == 200
                && (JsonNode.Parse(baseline.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                baseline.Status + " " + baseline.Body);
            var baselinePage = await ZbaAsync(http, "GET", root + "/events?after=0&limit=200", readToken);
            faultCursor = (JsonNode.Parse(baselinePage.Body) as JsonObject)?["nextSequence"]?.GetValue<long>() ?? -1;
            Check("S55 写失败前基线高水位为 1", faultCursor == 1, baselinePage.Status + " cursor=" + faultCursor);
            await bot.StopAsync();
        }

        // 只在本场景的合成数据目录内制造写失败：占住原子写的临时路径（空目录），不改产品、不碰真实数据。
        var faultBlocker = faultSequenceFile + ".tmp";
        if (!Path.GetFullPath(faultBlocker).StartsWith(
                Path.GetFullPath(faultDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("S55 合成故障路径越界");
        Directory.CreateDirectory(faultBlocker);
        Check("S55 合成写故障已注入（空目录占住临时路径）", Directory.Exists(faultBlocker), faultBlocker);

        using (var bot = StartBot(Env(faultPanel, faultWs, faultDir)))
        {
            await WaitForPortAsync(faultWs, cts.Token, bot);
            await WaitForPortAsync(faultPanel, cts.Token, bot);
            var root = $"http://127.0.0.1:{faultPanel}/zba/v1";
            var first = await SaveConfigAsync(root, 0);
            var second = await SaveConfigAsync(root, 1);
            Check("S55 序号写入失败时配置仍然提交",
                first.Status == 200
                && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && second.Status == 200
                && (JsonNode.Parse(second.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                first.Status + " " + first.Body + " | " + second.Status + " " + second.Body);

            var duringFault = await ZbaAsync(http, "GET", root + "/events?after=" + faultCursor + "&limit=200", readToken);
            var duringNode = JsonNode.Parse(duringFault.Body) as JsonObject;
            Check("S55 序号写入失败不确认未持久化游标，读侧要求 resync",
                duringFault.Status == 200
                && duringNode?["items"] is JsonArray { Count: 0 }
                && duringNode?["resyncRequired"]?.GetValue<bool>() == true
                && duringNode?["nextSequence"]?.GetValue<long>() == faultCursor,
                duringFault.Status + " " + duringFault.Body);

            var persisted = long.TryParse(
                (await File.ReadAllTextAsync(faultSequenceFile, cts.Token)).Trim(), out var stored) ? stored : -1;
            Check("S55 序号写入失败不推进持久化高水位", persisted == faultCursor, "persisted=" + persisted);

            var gapFile = faultSequenceFile + ".gap";
            var gapFloor = long.TryParse(
                File.Exists(gapFile) ? (await File.ReadAllTextAsync(gapFile, cts.Token)).Trim() : "",
                out var gap) ? gap : -1;
            Check("S55 序号写入失败会把缺口下限落盘（跨重启证据）",
                gapFloor == faultCursor + 1, "gap=" + gapFloor);
            await bot.StopAsync();
        }

        var faultLogPath = Path.Combine(faultDir, "logs", "qqchat.log");
        var faultLogged = File.Exists(faultLogPath)
                          && File.ReadLines(faultLogPath)
                              .Any(line => line.Contains("事件序号写入失败", StringComparison.Ordinal));
        Check("S55 序号写入失败会写安全日志", faultLogged, faultLogPath);

        Directory.Delete(faultBlocker);
        Check("S55 合成写故障已恢复（只删除本场景创建的空目录）", !Directory.Exists(faultBlocker), faultBlocker);

        using (var bot = StartBot(Env(faultPanelAfter, faultWsAfter, faultDir)))
        {
            await WaitForPortAsync(faultWsAfter, cts.Token, bot);
            await WaitForPortAsync(faultPanelAfter, cts.Token, bot);
            var root = $"http://127.0.0.1:{faultPanelAfter}/zba/v1";
            var first = await SaveConfigAsync(root, 0);
            var second = await SaveConfigAsync(root, 1);
            Check("S55 存储恢复并重启后配置保存成功",
                first.Status == 200
                && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && second.Status == 200
                && (JsonNode.Parse(second.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                first.Status + " " + first.Body + " | " + second.Status + " " + second.Body);

            var recovered = await ZbaAsync(http, "GET", root + "/events?after=" + faultCursor + "&limit=200", readToken);
            var recoveredNode = JsonNode.Parse(recovered.Body) as JsonObject;
            var sequences = (recoveredNode?["items"] as JsonArray)?
                .Select(item => item?["sequence"]?.GetValue<long>() ?? -1).ToArray() ?? Array.Empty<long>();
            Check("S55 恢复后新事件从持久化高水位 +1 起投递，不重用已确认序号",
                recovered.Status == 200
                && sequences.SequenceEqual(new[] { faultCursor + 2, faultCursor + 3 })
                && recoveredNode?["resyncRequired"]?.GetValue<bool>() == true,
                recovered.Status + " sequences=" + string.Join(",", sequences) + " " + recovered.Body);

            var atTip = await ZbaAsync(http, "GET", root + "/events?after=" + (faultCursor + 3), readToken);
            var tipNode = JsonNode.Parse(atTip.Body) as JsonObject;
            Check("S55 恢复后追平高水位不误报 resync",
                atTip.Status == 200
                && tipNode?["items"] is JsonArray { Count: 0 }
                && tipNode?["resyncRequired"]?.GetValue<bool>() == false,
                atTip.Status + " " + atTip.Body);
            await bot.StopAsync();
        }

        // 缺口标记跨重启（W5-G1-IO-R1）：故障期不轮询、重启时存储仍不可写，旧游标也必须拿到 resync。
        var missedDir = NewDataDir("s55-gap-missed");
        var missPanelA = FreePort(18131);
        var missWsA = FreePort(13081);
        var missPanelB = FreePort(18132);
        var missWsB = FreePort(13082);
        var missPanelC = FreePort(18133);
        var missWsC = FreePort(13083);
        var missPanelD = FreePort(18134);
        var missWsD = FreePort(13084);
        var missSequenceFile = Path.Combine(missedDir, "data", "zba-event-sequence");
        long missCursor;

        using (var bot = StartBot(Env(missPanelA, missWsA, missedDir)))
        {
            await WaitForPortAsync(missWsA, cts.Token, bot);
            await WaitForPortAsync(missPanelA, cts.Token, bot);
            var root = $"http://127.0.0.1:{missPanelA}/zba/v1";
            var baseline = await SaveConfigAsync(root, 1);
            var page = await ZbaAsync(http, "GET", root + "/events?after=0&limit=200", readToken);
            missCursor = (JsonNode.Parse(page.Body) as JsonObject)?["nextSequence"]?.GetValue<long>() ?? -1;
            Check("S55 缺口跨重启基线保存成功且高水位为 1",
                baseline.Status == 200
                && (JsonNode.Parse(baseline.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && missCursor == 1,
                baseline.Status + " cursor=" + missCursor);
            await bot.StopAsync();
        }

        var missBlocker = missSequenceFile + ".tmp";
        if (!Path.GetFullPath(missBlocker).StartsWith(
                Path.GetFullPath(missedDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("S55 缺口负例故障路径越界");
        Directory.CreateDirectory(missBlocker);

        using (var bot = StartBot(Env(missPanelB, missWsB, missedDir)))
        {
            await WaitForPortAsync(missWsB, cts.Token, bot);
            await WaitForPortAsync(missPanelB, cts.Token, bot);
            var root = $"http://127.0.0.1:{missPanelB}/zba/v1";
            var first = await SaveConfigAsync(root, 0);
            var second = await SaveConfigAsync(root, 1);
            Check("S55 缺口跨重启：故障期配置仍提交（客户端不轮询事件）",
                first.Status == 200
                && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && second.Status == 200
                && (JsonNode.Parse(second.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                first.Status + " " + first.Body + " | " + second.Status + " " + second.Body);
            await bot.StopAsync();
        }

        using (var bot = StartBot(Env(missPanelC, missWsC, missedDir)))
        {
            await WaitForPortAsync(missWsC, cts.Token, bot);
            await WaitForPortAsync(missPanelC, cts.Token, bot);
            var root = $"http://127.0.0.1:{missPanelC}/zba/v1";
            Check("S55 缺口跨重启：重启时存储仍不可写", Directory.Exists(missBlocker), missBlocker);
            var page = await ZbaAsync(http, "GET", root + "/events?after=" + missCursor + "&limit=200", readToken);
            var node = JsonNode.Parse(page.Body) as JsonObject;
            Check("S55 缺口跨重启：未轮询故障期，重启后旧游标仍要求 resync",
                page.Status == 200
                && node?["items"] is JsonArray { Count: 0 }
                && node?["nextSequence"]?.GetValue<long>() == missCursor
                && node?["resyncRequired"]?.GetValue<bool>() == true,
                page.Status + " " + page.Body);
            await bot.StopAsync();
        }

        Directory.Delete(missBlocker);

        using (var bot = StartBot(Env(missPanelD, missWsD, missedDir)))
        {
            await WaitForPortAsync(missWsD, cts.Token, bot);
            await WaitForPortAsync(missPanelD, cts.Token, bot);
            var root = $"http://127.0.0.1:{missPanelD}/zba/v1";
            var first = await SaveConfigAsync(root, 0);
            var second = await SaveConfigAsync(root, 1);
            var page = await ZbaAsync(http, "GET", root + "/events?after=" + missCursor + "&limit=200", readToken);
            var node = JsonNode.Parse(page.Body) as JsonObject;
            var sequences = (node?["items"] as JsonArray)?
                .Select(item => item?["sequence"]?.GetValue<long>() ?? -1).ToArray() ?? Array.Empty<long>();
            Check("S55 恢复写入后缺口标记仍在：新序号续写且旧游标 resync",
                first.Status == 200 && second.Status == 200
                && sequences.SequenceEqual(new[] { missCursor + 2, missCursor + 3 })
                && node?["resyncRequired"]?.GetValue<bool>() == true,
                page.Status + " sequences=" + string.Join(",", sequences)
                + " resync=" + node?["resyncRequired"] + " " + page.Body);
            await bot.StopAsync();
        }

        // 健康首次安装：空数据根没有任何已提交配置，启动标记本次写成，不设屏障。
        var freshDir = NewDataDir("s55-first-boot");
        var freshPanel = FreePort(18135);
        var freshWs = FreePort(13085);
        using (var bot = StartBot(Env(freshPanel, freshWs, freshDir)))
        {
            await WaitForPortAsync(freshWs, cts.Token, bot);
            await WaitForPortAsync(freshPanel, cts.Token, bot);
            var root = $"http://127.0.0.1:{freshPanel}/zba/v1";
            var fresh = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var freshNode = JsonNode.Parse(fresh.Body) as JsonObject;
            Check("S55 健康首次安装：空数据根零游标不要求 resync",
                fresh.Status == 200
                && freshNode?["items"] is JsonArray { Count: 0 }
                && freshNode?["nextSequence"]?.GetValue<long>() == 0
                && freshNode?["resyncRequired"]?.GetValue<bool>() == false,
                fresh.Status + " " + fresh.Body);

            var first = await SaveConfigAsync(root, 1);
            var page = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var pageNode = JsonNode.Parse(page.Body) as JsonObject;
            var pageSequences = (pageNode?["items"] as JsonArray)?
                .Select(item => item?["sequence"]?.GetValue<long>() ?? -1).ToArray() ?? Array.Empty<long>();
            Check("S55 健康首次安装：首个事件从 1 起投递且不误报 resync",
                first.Status == 200
                && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && pageSequences.SequenceEqual(new[] { 1L })
                && pageNode?["resyncRequired"]?.GetValue<bool>() == false,
                page.Status + " sequences=" + string.Join(",", pageSequences) + " " + page.Body);
            await bot.StopAsync();
        }

        // W5-G1-BOOT-LOSS：序号/缺口/启动标记三个临时路径全部写失败，配置却提交了两次。
        // 恢复后重启不能把“标记缺失且现在可写”当成首次安装：旧游标必须 resync。
        var bootLossDir = NewDataDir("s55-boot-loss");
        var bootLossSequence = Path.Combine(bootLossDir, "data", "zba-event-sequence");
        var bootLossPanelBefore = FreePort(18136);
        var bootLossWsBefore = FreePort(13086);
        var bootLossPanelAfter = FreePort(18137);
        var bootLossWsAfter = FreePort(13087);
        var bootLossBlockers = BlockMetadataWrites(bootLossDir, bootLossSequence);
        int bootLossVersion;
        using (var bot = StartBot(Env(bootLossPanelBefore, bootLossWsBefore, bootLossDir)))
        {
            await WaitForPortAsync(bootLossWsBefore, cts.Token, bot);
            await WaitForPortAsync(bootLossPanelBefore, cts.Token, bot);
            var root = $"http://127.0.0.1:{bootLossPanelBefore}/zba/v1";
            var initial = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var initialNode = JsonNode.Parse(initial.Body) as JsonObject;
            Check("S55 三元元数据写失败：首个进程内旧游标即要求 resync",
                initial.Status == 200
                && initialNode?["items"] is JsonArray { Count: 0 }
                && initialNode?["nextSequence"]?.GetValue<long>() == 0
                && initialNode?["resyncRequired"]?.GetValue<bool>() == true,
                initial.Status + " " + initial.Body);

            bootLossVersion = await ReadConfigVersionAsync(root);
            var first = await SaveConfigAsync(root, 1);
            var second = await SaveConfigAsync(root, 0);
            var versionAfter = await ReadConfigVersionAsync(root);
            Check("S55 三元元数据写失败：配置仍然提交",
                bootLossVersion >= 0 && versionAfter == bootLossVersion + 2
                && first.Status == 200
                && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && second.Status == 200
                && (JsonNode.Parse(second.Body) as JsonObject)?["code"]?.GetValue<int>() == 0,
                first.Status + " " + first.Body + " | " + second.Status + " " + second.Body
                + " version=" + versionAfter);
            Check("S55 三元元数据写失败：三个事件元数据文件均未写成",
                !File.Exists(bootLossSequence)
                && !File.Exists(bootLossSequence + ".gap")
                && !File.Exists(bootLossSequence + ".boot")
                && bootLossBlockers.All(Directory.Exists),
                $"sequence={File.Exists(bootLossSequence)} gap={File.Exists(bootLossSequence + ".gap")}"
                + $" boot={File.Exists(bootLossSequence + ".boot")}");
            await bot.StopAsync();
        }

        foreach (var blocker in bootLossBlockers) Directory.Delete(blocker);
        Check("S55 三元元数据故障已恢复（只删除本场景创建的空目录）",
            bootLossBlockers.All(blocker => !Directory.Exists(blocker)), string.Join(";", bootLossBlockers));

        using (var bot = StartBot(Env(bootLossPanelAfter, bootLossWsAfter, bootLossDir)))
        {
            await WaitForPortAsync(bootLossWsAfter, cts.Token, bot);
            await WaitForPortAsync(bootLossPanelAfter, cts.Token, bot);
            var root = $"http://127.0.0.1:{bootLossPanelAfter}/zba/v1";
            var retained = await ReadConfigVersionAsync(root);
            Check("S55 三元元数据恢复后配置版本仍保留", retained == bootLossVersion + 2, "version=" + retained);

            var page = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var node = JsonNode.Parse(page.Body) as JsonObject;
            Check("S55 启动标记缺失但已有已提交配置：重启后旧游标必须 resync",
                page.Status == 200
                && node?["items"] is JsonArray { Count: 0 }
                && node?["nextSequence"]?.GetValue<long>() == 0
                && node?["resyncRequired"]?.GetValue<bool>() == true,
                page.Status + " " + page.Body);

            var first = await SaveConfigAsync(root, 1);
            var second = await SaveConfigAsync(root, 0);
            var after = await ZbaAsync(http, "GET", root + "/events?after=0&limit=200", readToken);
            var afterNode = JsonNode.Parse(after.Body) as JsonObject;
            var sequences = (afterNode?["items"] as JsonArray)?
                .Select(item => item?["sequence"]?.GetValue<long>() ?? -1).ToArray() ?? Array.Empty<long>();
            var persisted = File.Exists(bootLossSequence)
                ? (await File.ReadAllTextAsync(bootLossSequence, cts.Token)).Trim()
                : string.Empty;
            Check("S55 电平缺口下新事件仍从 1 起投递，不凭空预留序号",
                first.Status == 200 && second.Status == 200
                && sequences.SequenceEqual(new[] { 1L, 2L })
                && persisted == "2",
                after.Status + " sequences=" + string.Join(",", sequences) + " persisted=" + persisted);
            Check("S55 恢复写入后漏掉的已提交窗口仍要求 resync（新事件不能抹掉缺口）",
                after.Status == 200 && afterNode?["resyncRequired"]?.GetValue<bool>() == true,
                after.Status + " " + after.Body);

            var adoptedPage = await ZbaAsync(http, "GET", root + "/events?after=2", readToken);
            var adoptedNode = JsonNode.Parse(adoptedPage.Body) as JsonObject;
            Check("S55 电平缺口下采用新游标后完成 resync",
                adoptedPage.Status == 200
                && adoptedNode?["items"] is JsonArray { Count: 0 }
                && adoptedNode?["resyncRequired"]?.GetValue<bool>() == false,
                adoptedPage.Status + " " + adoptedPage.Body);
            await bot.StopAsync();
        }

        // 延迟恢复：第一次重启时三个元数据仍不可写，第二次重启才全部恢复，仍不得误判首次启动。
        var bootDelayDir = NewDataDir("s55-boot-delay");
        var bootDelaySequence = Path.Combine(bootDelayDir, "data", "zba-event-sequence");
        var bootDelayPanelA = FreePort(18138);
        var bootDelayWsA = FreePort(13088);
        var bootDelayPanelB = FreePort(18139);
        var bootDelayWsB = FreePort(13089);
        var bootDelayPanelC = FreePort(18140);
        var bootDelayWsC = FreePort(13090);
        var bootDelayBlockers = BlockMetadataWrites(bootDelayDir, bootDelaySequence);
        int bootDelayVersion;
        using (var bot = StartBot(Env(bootDelayPanelA, bootDelayWsA, bootDelayDir)))
        {
            await WaitForPortAsync(bootDelayWsA, cts.Token, bot);
            await WaitForPortAsync(bootDelayPanelA, cts.Token, bot);
            var root = $"http://127.0.0.1:{bootDelayPanelA}/zba/v1";
            bootDelayVersion = await ReadConfigVersionAsync(root);
            var first = await SaveConfigAsync(root, 1);
            var second = await SaveConfigAsync(root, 0);
            Check("S55 延迟恢复：故障期配置提交两次",
                bootDelayVersion >= 0
                && first.Status == 200
                && (JsonNode.Parse(first.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && second.Status == 200
                && (JsonNode.Parse(second.Body) as JsonObject)?["code"]?.GetValue<int>() == 0
                && await ReadConfigVersionAsync(root) == bootDelayVersion + 2,
                first.Status + " " + first.Body + " | " + second.Status + " " + second.Body);
            await bot.StopAsync();
        }

        using (var bot = StartBot(Env(bootDelayPanelB, bootDelayWsB, bootDelayDir)))
        {
            await WaitForPortAsync(bootDelayWsB, cts.Token, bot);
            await WaitForPortAsync(bootDelayPanelB, cts.Token, bot);
            var root = $"http://127.0.0.1:{bootDelayPanelB}/zba/v1";
            var page = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var node = JsonNode.Parse(page.Body) as JsonObject;
            Check("S55 延迟恢复：存储仍不可写时旧游标仍要求 resync",
                page.Status == 200
                && node?["items"] is JsonArray { Count: 0 }
                && node?["nextSequence"]?.GetValue<long>() == 0
                && node?["resyncRequired"]?.GetValue<bool>() == true,
                page.Status + " " + page.Body);
            await bot.StopAsync();
        }

        foreach (var blocker in bootDelayBlockers) Directory.Delete(blocker);

        using (var bot = StartBot(Env(bootDelayPanelC, bootDelayWsC, bootDelayDir)))
        {
            await WaitForPortAsync(bootDelayWsC, cts.Token, bot);
            await WaitForPortAsync(bootDelayPanelC, cts.Token, bot);
            var root = $"http://127.0.0.1:{bootDelayPanelC}/zba/v1";
            var retained = await ReadConfigVersionAsync(root);
            var page = await ZbaAsync(http, "GET", root + "/events?after=0", readToken);
            var node = JsonNode.Parse(page.Body) as JsonObject;
            Check("S55 延迟恢复：全部恢复后重启仍不把已有配置当首次启动",
                retained == bootDelayVersion + 2
                && page.Status == 200
                && node?["resyncRequired"]?.GetValue<bool>() == true,
                page.Status + " version=" + retained + " " + page.Body);
            await bot.StopAsync();
        }

        CheckEventRingOverflow();
        CheckSequenceWriteFailureDegrades();

        async Task<int> ReadConfigVersionAsync(string root)
        {
            var read = await ZbaAsync(http, "POST", root + "/configuration/read", readToken, """{"kind":0}""");
            return int.TryParse((JsonNode.Parse(read.Body) as JsonObject)?["version"]?.GetValue<string>(),
                out var version) ? version : -1;
        }

        string[] BlockMetadataWrites(string dataRoot, string sequencePath)
        {
            var blockers = new[]
            {
                sequencePath + ".tmp",
                sequencePath + ".gap.tmp",
                sequencePath + ".boot.tmp"
            };
            foreach (var blocker in blockers)
            {
                if (!Path.GetFullPath(blocker).StartsWith(
                        Path.GetFullPath(dataRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("S55 事件元数据故障路径越界");
                Directory.CreateDirectory(blocker);
            }

            return blockers;
        }
    }

    /// <summary>
    /// 序号写入失败（W5-G1-IO）的确定性负例：直接反射构造真实 store + buffer，不依赖进程与端口。
    /// 断言失败时不发布事件、高水位不推进、读侧要求 resync；恢复写入后从同一序号续写，
    /// 且缺口之前的游标仍要求 resync。
    /// </summary>
    private static void CheckSequenceWriteFailureDegrades()
    {
        var assembly = Assembly.LoadFrom(BotDll);
        var storeType = assembly.GetType("BotAgent.Adapters.Persistence.ZbaEventSequenceStore", throwOnError: true)!;
        var bufferType = assembly.GetType("BotAgent.Adapters.Panel.Zba.ZbaEventBuffer", throwOnError: true)!;
        var dir = NewDataDir("s55-seq-store");
        var path = Path.Combine(dir, "zba-event-sequence");
        var now = DateTimeOffset.UtcNow;
        var store = Activator.CreateInstance(storeType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { path, (Func<DateTimeOffset>)(() => now), null }, culture: null)!;
        var buffer = Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { store }, culture: null)!;
        var append = bufferType.GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var read = bufferType.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Directory.CreateDirectory(path + ".tmp");
        var published = (bool)append.Invoke(buffer,
            new object?[] { "task.updated", "zt-synthetic", "queued", null, null, now })!;
        var faulted = read.Invoke(buffer, new object?[] { 0L, 200, now })!;
        var faultedItems = (System.Collections.ICollection)faulted.GetType().GetProperty("Items")!.GetValue(faulted)!;
        var faultedResync = (bool)faulted.GetType().GetProperty("ResyncRequired")!.GetValue(faulted)!;
        var faultedNext = (long)faulted.GetType().GetProperty("NextSequence")!.GetValue(faulted)!;
        Check("S55 序号写失败：事件不发布、高水位不推进、读侧要求 resync",
            !published && faultedItems.Count == 0 && faultedNext == 0 && faultedResync,
            $"published={published} items={faultedItems.Count} next={faultedNext} resync={faultedResync}");

        // 缺口下限必须落盘，并由新实例（模拟重启）加载，否则离线客户端的旧游标会被当成已追平。
        var gapMarker = path + ".gap";
        var persistedGap = File.Exists(gapMarker) ? File.ReadAllText(gapMarker).Trim() : "";
        var reloaded = Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { store }, culture: null)!;
        var afterRestart = read.Invoke(reloaded, new object?[] { 0L, 200, now })!;
        var restartItems = (System.Collections.ICollection)afterRestart.GetType().GetProperty("Items")!.GetValue(afterRestart)!;
        var restartResync = (bool)afterRestart.GetType().GetProperty("ResyncRequired")!.GetValue(afterRestart)!;
        var restartNext = (long)afterRestart.GetType().GetProperty("NextSequence")!.GetValue(afterRestart)!;
        Check("S55 缺口下限落盘并被新实例加载（写入仍不可用时保持电平 resync）",
            persistedGap == "1" && restartItems.Count == 0 && restartNext == 0 && restartResync,
            $"gap={persistedGap} items={restartItems.Count} next={restartNext} resync={restartResync}");

        Directory.Delete(path + ".tmp");
        var recovered = (bool)append.Invoke(buffer,
            new object?[] { "task.updated", "zt-synthetic", "queued", null, null, now })!;
        var healed = read.Invoke(buffer, new object?[] { 0L, 200, now })!;
        var healedItems = (System.Collections.ICollection)healed.GetType().GetProperty("Items")!.GetValue(healed)!;
        var healedResync = (bool)healed.GetType().GetProperty("ResyncRequired")!.GetValue(healed)!;
        var healedNext = (long)healed.GetType().GetProperty("NextSequence")!.GetValue(healed)!;
        var persisted = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        Check("S55 序号写失败恢复：从同一序号续写，缺口前游标仍要求 resync",
            recovered && persisted == "1" && healedItems.Count == 1 && healedNext == 1 && healedResync,
            $"published={recovered} persisted={persisted} items={healedItems.Count} next={healedNext} resync={healedResync}");

        // 第二次缺口：缺口序号必须前移到 2，否则停在旧缺口的游标（after=1）会被误判为“已追平”。
        Directory.CreateDirectory(path + ".tmp");
        var secondFault = (bool)append.Invoke(buffer,
            new object?[] { "task.updated", "zt-synthetic", "running", null, null, now })!;
        var second = read.Invoke(buffer, new object?[] { 1L, 200, now })!;
        var secondItems = (System.Collections.ICollection)second.GetType().GetProperty("Items")!.GetValue(second)!;
        var secondResync = (bool)second.GetType().GetProperty("ResyncRequired")!.GetValue(second)!;
        var secondNext = (long)second.GetType().GetProperty("NextSequence")!.GetValue(second)!;
        Check("S55 第二次写失败推进缺口，缺口前游标仍要求 resync",
            !secondFault && secondItems.Count == 0 && secondNext == 1 && secondResync,
            $"published={secondFault} items={secondItems.Count} next={secondNext} resync={secondResync}");

        // 启动屏障：已有高水位 3 的新实例即使没有缺口标记，也必须让旧游标先 resync 一次。
        var barrierDir = NewDataDir("s55-seq-barrier");
        var barrierPath = Path.Combine(barrierDir, "zba-event-sequence");
        File.WriteAllText(barrierPath, "3");
        var barrierStore = Activator.CreateInstance(storeType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { barrierPath, (Func<DateTimeOffset>)(() => now), null }, culture: null)!;
        var barrierBuffer = Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { barrierStore }, culture: null)!;
        var barrierPage = read.Invoke(barrierBuffer, new object?[] { 3L, 200, now })!;
        var barrierItems = (System.Collections.ICollection)barrierPage.GetType().GetProperty("Items")!.GetValue(barrierPage)!;
        var barrierResync = (bool)barrierPage.GetType().GetProperty("ResyncRequired")!.GetValue(barrierPage)!;
        var barrierNext = (long)barrierPage.GetType().GetProperty("NextSequence")!.GetValue(barrierPage)!;
        Check("S55 启动屏障：重启后旧游标==高水位也要求 resync，并推进到可完成的游标",
            barrierItems.Count == 0 && barrierNext == 4 && barrierResync,
            $"items={barrierItems.Count} next={barrierNext} resync={barrierResync}");

        // W5-G1-BOOT-LOSS：标记本次写成 + 已有已提交配置 = 不是首次安装。
        // 高水位为 0 时用缺口下限让旧游标重读，真实事件仍从 1 开始（不预留不存在的序号）。
        var evidenceDir = NewDataDir("s55-seq-boot-evidence");
        var evidencePath = Path.Combine(evidenceDir, "zba-event-sequence");
        var evidenceStore = Activator.CreateInstance(storeType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { evidencePath, (Func<DateTimeOffset>)(() => now), null }, culture: null)!;
        var evidenceBuffer = Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { evidenceStore, true }, culture: null)!;
        var evidencePage = read.Invoke(evidenceBuffer, new object?[] { 0L, 200, now })!;
        var evidenceItems = (System.Collections.ICollection)evidencePage.GetType().GetProperty("Items")!.GetValue(evidencePage)!;
        var evidenceResync = (bool)evidencePage.GetType().GetProperty("ResyncRequired")!.GetValue(evidencePage)!;
        var evidenceNext = (long)evidencePage.GetType().GetProperty("NextSequence")!.GetValue(evidencePage)!;
        var evidenceGap = File.Exists(evidencePath + ".gap") ? File.ReadAllText(evidencePath + ".gap").Trim() : "";
        Check("S55 标记本次写成但已有已提交配置：电平缺口要求 resync 且不预留序号",
            evidenceItems.Count == 0 && evidenceNext == 0 && evidenceResync
            && evidenceGap == "1" && !File.Exists(evidencePath),
            $"items={evidenceItems.Count} next={evidenceNext} resync={evidenceResync} gap={evidenceGap}");

        var evidenceAppend = (bool)append.Invoke(evidenceBuffer,
            new object?[] { "configuration.saved", null, "applied", "2", null, now })!;
        var evidenceAfterEvent = read.Invoke(evidenceBuffer, new object?[] { 0L, 200, now })!;
        var evidenceAfterItems = (System.Collections.ICollection)evidenceAfterEvent.GetType().GetProperty("Items")!.GetValue(evidenceAfterEvent)!;
        var evidenceAfterResync = (bool)evidenceAfterEvent.GetType().GetProperty("ResyncRequired")!.GetValue(evidenceAfterEvent)!;
        var evidenceAfterNext = (long)evidenceAfterEvent.GetType().GetProperty("NextSequence")!.GetValue(evidenceAfterEvent)!;
        var evidencePersisted = File.Exists(evidencePath) ? File.ReadAllText(evidencePath).Trim() : "";
        Check("S55 电平缺口下首个事件从 1 起投递且仍要求 resync",
            evidenceAppend && evidenceAfterItems.Count == 1 && evidenceAfterNext == 1
            && evidenceAfterResync && evidencePersisted == "1",
            $"appended={evidenceAppend} items={evidenceAfterItems.Count} next={evidenceAfterNext}"
            + $" resync={evidenceAfterResync} persisted={evidencePersisted}");

        var evidenceAdopted = read.Invoke(evidenceBuffer, new object?[] { 1L, 200, now })!;
        var evidenceAdoptedItems = (System.Collections.ICollection)evidenceAdopted.GetType().GetProperty("Items")!.GetValue(evidenceAdopted)!;
        var evidenceAdoptedResync = (bool)evidenceAdopted.GetType().GetProperty("ResyncRequired")!.GetValue(evidenceAdopted)!;
        Check("S55 反射：电平缺口下采用新游标后完成 resync",
            evidenceAdoptedItems.Count == 0 && !evidenceAdoptedResync,
            $"items={evidenceAdoptedItems.Count} resync={evidenceAdoptedResync}");

        // 对照：没有任何已提交配置时，标记本次写成仍是健康首次安装，不设屏障。
        var firstBootDir = NewDataDir("s55-seq-first-boot");
        var firstBootPath = Path.Combine(firstBootDir, "zba-event-sequence");
        var firstBootStore = Activator.CreateInstance(storeType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { firstBootPath, (Func<DateTimeOffset>)(() => now), null }, culture: null)!;
        var firstBootBuffer = Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { firstBootStore, false }, culture: null)!;
        var firstBootPage = read.Invoke(firstBootBuffer, new object?[] { 0L, 200, now })!;
        var firstBootItems = (System.Collections.ICollection)firstBootPage.GetType().GetProperty("Items")!.GetValue(firstBootPage)!;
        var firstBootResync = (bool)firstBootPage.GetType().GetProperty("ResyncRequired")!.GetValue(firstBootPage)!;
        var firstBootNext = (long)firstBootPage.GetType().GetProperty("NextSequence")!.GetValue(firstBootPage)!;
        Check("S55 健康首次安装不设屏障，零游标不要求 resync",
            firstBootItems.Count == 0 && firstBootNext == 0 && !firstBootResync,
            $"items={firstBootItems.Count} next={firstBootNext} resync={firstBootResync}");
    }

    /// <summary>
    /// 512 环溢出不依赖 513 次 HTTP 保存：直接反射构造真实 ZbaEventBuffer 追加 520 条，
    /// 断言旧游标被淘汰后明确 resync，且 limit 仍生效。
    /// </summary>
    private static void CheckEventRingOverflow()
    {
        var assembly = Assembly.LoadFrom(BotDll);
        var type = assembly.GetType("BotAgent.Adapters.Panel.Zba.ZbaEventBuffer", throwOnError: true)!;
        var buffer = Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: new object?[] { null }, culture: null)!;
        var append = type.GetMethod("Append", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var read = type.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTimeOffset.UtcNow;
        for (var index = 1; index <= 520; index++)
        {
            append.Invoke(buffer, new object?[] { "task.updated", "zt-synthetic", "queued", null, null, now });
        }

        var overflow = read.Invoke(buffer, new object?[] { 0L, 200, now })!;
        var overflowItems = (System.Collections.ICollection)overflow.GetType().GetProperty("Items")!.GetValue(overflow)!;
        var overflowResync = (bool)overflow.GetType().GetProperty("ResyncRequired")!.GetValue(overflow)!;
        var overflowNext = (long)overflow.GetType().GetProperty("NextSequence")!.GetValue(overflow)!;
        Check("S55 环溢出后旧游标要求 resync 且 limit 生效",
            overflowItems.Count == 200 && overflowResync && overflowNext == 208,
            $"items={overflowItems.Count} resync={overflowResync} next={overflowNext}");

        var tip = read.Invoke(buffer, new object?[] { 519L, 200, now })!;
        var tipItems = (System.Collections.ICollection)tip.GetType().GetProperty("Items")!.GetValue(tip)!;
        var tipResync = (bool)tip.GetType().GetProperty("ResyncRequired")!.GetValue(tip)!;
        Check("S55 环内游标不误报 resync",
            tipItems.Count == 1 && !tipResync,
            $"items={tipItems.Count} resync={tipResync}");
    }
}
