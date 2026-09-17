using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace QQChatAgent.Services.Agent;

/// <summary>
/// **服务器自己**的 agent：跑在机器人容器里，用模型 + 工具循环干活（号主 2026-09-17 要的“bot 自己也要有 agent 能力”）。
///
/// 为什么不用“在容器里再装一个 pi”：
///   • 镜像要加 Node + npm + pi（几百 MB），而容器里真正缺的是**干活的脑子**，不是那个 CLI；
///   • pi 的价值在号主本机的文件/工具链 —— 那是**本机桥**（AgentBridgeServer）的事；
///   • 容器里的 agent 要干的是“看日志、翻数据、查库存、跑健康检查”这类运维活：bash + 读写文件 + 抓 URL 就够。
/// 所以这里是一个**原生 C# 的工具循环**：同一个模型、同一套白名单，只是执行地点在容器里。
///
/// 工具循环长什么样（故意不用 function-calling，跟机器人其它地方一个口径：模型吐 JSON）：
///   user: 用户那句 //指令（+ 现场信息：容器里有什么工具、工作目录）
///   → 模型回 {"thought":"…","tool":"bash","command":"ls -la /data"}  → 真跑 → 输出喂回去 → 再问
///   → 直到 {"final":"……"} 或步数用完
/// </summary>
public sealed class ServerAgentRunner
{
    private readonly AppSettings _settings;
    private readonly OpenAiClient _brain;
    private readonly Action<string> _log;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public ServerAgentRunner(AppSettings settings, OpenAiClient brain, Action<string> log)
    {
        _settings = settings;
        _brain = brain;
        _log = log;
    }

    /// <summary>进度（工具调用）—— 与桥同一套事件，BotAgent 那边不用分叉。</summary>
    public event Action<AgentTask>? Progress;

    /// <summary>跑一个任务（把结果写回 task）。调用方负责在后台线程里跑它。</summary>
    public async Task RunAsync(AgentTask task, CancellationToken ct)
    {
        var workDir = string.IsNullOrWhiteSpace(_settings.AgentServerWorkDir) ? "/data" : _settings.AgentServerWorkDir.Trim();
        var maxSteps = Math.Clamp(_settings.AgentServerMaxSteps, 1, 30);
        var allowed = ParseTools(_settings.AgentServerTools);

        var system = BuildSystemPrompt(workDir, allowed);
        var messages = new List<(string Role, string Text)> { ("user", task.Prompt) };
        var started = DateTimeOffset.Now;

        try
        {
            for (var step = 1; step <= maxSteps; step++)
            {
                if (ct.IsCancellationRequested || task.CancelRequested)
                {
                    task.Fail("已取消");
                    return;
                }

                var raw = await _brain.CompleteChatAsync(
                    _settings.AgentServerModel, system, messages, _settings.AgentServerMaxTokens, 0.3, ct,
                    baseUrlOverride: _settings.AgentServerBaseUrl, apiKeyOverride: _settings.AgentServerApiKey);

                if (string.IsNullOrWhiteSpace(raw))
                {
                    task.Fail("模型没有返回内容（看日志的 Agent 行）");
                    return;
                }

                var call = ParseStep(raw);
                if (call is null)
                {
                    // 模型没说清楚（不是 JSON）：把这段当结论，别再空转
                    task.Succeeded(raw.Trim());
                    return;
                }

                var (tool, arg, command, final) = call.Value;
                if (final is { Length: > 0 })
                {
                    task.ToolCalls = step - 1;
                    task.Succeeded(final.Trim());
                    return;
                }

                if (tool is null)
                {
                    task.Succeeded(raw.Trim());
                    return;
                }

                var name = tool;
                task.LastNote = DescribeTool(name, command, arg);
                task.ToolCalls = step;
                Progress?.Invoke(task);

                string output;
                try
                {
                    output = await RunToolAsync(name, arg, command, workDir, allowed, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    output = $"工具执行出错：{ex.GetType().Name} {ex.Message}";
                }

                _log($"[ServerAgent] 第 {step} 步 {task.LastNote} → {Shorten(output.Replace('\n', ' '), 110)}");

                messages.Add(("assistant", raw));
                messages.Add(("user", $"工具输出（{name}）：\n{output}\n\n（继续：要么再调工具，要么给 final）"));
            }

            task.Fail($"跑了 {maxSteps} 步还没给出结论（把活拆小一点，或者让我再试一次）");
        }
        catch (OperationCanceledException)
        {
            task.Fail("已取消或超时");
        }
        catch (Exception ex)
        {
            task.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 面板/群里都要显示“跑了多久”，服务器这条路不能被落下
            if (task.DurationMs == 0)
            {
                task.DurationMs = (long)(DateTimeOffset.Now - started).TotalMilliseconds;
            }
        }
    }

    private string BuildSystemPrompt(string workDir, HashSet<string> allowed)
    {
        var list = new List<string>();
        if (allowed.Contains("bash"))
        {
            list.Add("bash：在容器里跑一条 shell 命令（command 字段）。容器是 Debian 12，有 bash/sh/grep/sed/awk/tar/grep，**没有** python/curl/git/node/jq。");
        }

        if (allowed.Contains("read"))
        {
            list.Add("read：读一个文本文件（path 字段，最多 4000 字）。");
        }

        if (allowed.Contains("write"))
        {
            list.Add("write：写一个文本文件（path + content；会覆盖）。");
        }

        if (allowed.Contains("fetch"))
        {
            list.Add("fetch：抓一个 http(s) 地址的正文（url 字段，最多 4000 字）。");
        }

        var tools = list.Count == 0 ? "（这次一个工具都没开，只能凭已知信息回答）" : string.Join("\n", list);

        return
            "你是运行在 QQ 机器人**服务器容器内部**的执行 agent。你的活儿是看日志、翻数据、查文件、跑健康检查这类运维/排查工作。\n" +
            $"工作目录：{workDir}（机器人自己的数据目录是 /data：日志 /data/logs/qqchat.log、数据库 /data/data/qqchat.db）\n" +
            $"可用工具：\n{tools}\n\n" +
            "每一轮**只输出一行 JSON**，两种形状之一：\n" +
            "  {\"thought\":\"我现在想干什么\",\"tool\":\"bash\",\"command\":\"ls -la /data\"}\n" +
            "  {\"final\":\"给群友看的结论\"}\n" +
            "规则：\n" +
            "• 一次只做一件事；看到输出不够就再来一步，够了就给 final。\n" +
            "• 命令要短、要有界（别跑 `tail -f`、别跑长时间的循环）；不确定的目录先 `ls`。\n" +
            "• final 里写**人话结论**（群里的人只看这一条），带上关键证据（数字、路径、报错原文片段）。\n" +
            "• 别编：命令没输出就说没输出；不确定就说不确定。\n" +
            "• 不要试图联网装东西（容器里没包管理器权限），也不要改机器人自己的代码/数据 —— 只读为主，" +
            "除非用户明确要求写文件。" +
            (_settings.AgentServerBaseUrl is { Length: > 0 } ? $"\n（你的模型接口：{_settings.AgentServerBaseUrl}，模型 {(_settings.AgentServerModel.Length > 0 ? _settings.AgentServerModel : _settings.Model)}）" : string.Empty);
    }

    private static HashSet<string> ParseTools(string raw)
    {
        var all = new[] { "bash", "read", "write", "fetch" };
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(all);
        }

        var set = new HashSet<string>();
        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = piece.Trim().ToLowerInvariant();
            if (all.Contains(name))
            {
                set.Add(name);
            }
        }

        return set;
    }

    /// <summary>解析模型这一轮的 JSON（宽容：外面带解释、带代码围栏都认）。</summary>
    private static (string? Tool, string? Arg, string? Command, string? Final)? ParseStep(string raw)
    {
        var text = raw.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text[start..(end + 1)]);
        }
        catch
        {
            return null;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        var tool = obj["tool"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? obj["name"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(tool))
        {
            var final = obj["final"]?.GetValue<string>() ?? obj["answer"]?.GetValue<string>() ?? obj["reply"]?.GetValue<string>();
            return (null, null, null, final);
        }

        var arg = obj["path"]?.GetValue<string>() ?? obj["url"]?.GetValue<string>() ?? obj["file"]?.GetValue<string>();
        var command = obj["command"]?.GetValue<string>() ?? obj["cmd"]?.GetValue<string>() ?? obj["content"]?.GetValue<string>();
        var finalText = obj["final"]?.GetValue<string>();
        return (tool, arg, command, finalText);
    }

    private async Task<string> RunToolAsync(string tool, string? arg, string? command, string workDir, HashSet<string> allowed, CancellationToken ct)
    {
        if (!allowed.Contains(tool))
        {
            return $"工具 {tool} 没开（当前只允许：{string.Join(", ", allowed)}）";
        }

        switch (tool)
        {
            case "bash":
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    return "缺少 command 字段";
                }

                return await RunShellAsync(command!, workDir, ct);
            }

            case "read":
            {
                var path = Resolve(arg, workDir);
                if (path is null)
                {
                    return "缺少 path 字段";
                }

                if (!File.Exists(path))
                {
                    return $"文件不存在：{path}";
                }

                var text = await File.ReadAllTextAsync(path, ct);
                return Truncate(text, 4000, "（文件太大，只给了前 4000 字）");
            }

            case "write":
            {
                var path = Resolve(arg, workDir);
                if (path is null || command is null)
                {
                    return "write 需要 path 和 content";
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                await File.WriteAllTextAsync(path, command, ct);
                return $"已写入 {path}（{command.Length} 字）";
            }

            case "fetch":
            {
                if (string.IsNullOrWhiteSpace(arg) || !Uri.TryCreate(arg, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return "fetch 需要一个 http(s) 地址";
                }

                using var response = await Http.GetAsync(uri, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                return $"HTTP {(int)response.StatusCode}\n" + Truncate(body, 4000, "（正文太长，只给了前 4000 字）");
            }

            default:
                return $"不认识工具 {tool}（可用：{string.Join(", ", allowed)}）";
        }
    }

    /// <summary>跑一条 shell 命令：bash -lc，超时/输出上限都卡住（容器里跑飞了会拖垮机器人）。</summary>
    private async Task<string> RunShellAsync(string command, string workDir, CancellationToken ct)
    {
        var timeout = Math.Clamp(_settings.AgentServerCommandTimeoutSeconds, 5, 300);
        var psi = new ProcessStartInfo("bash")
        {
            WorkingDirectory = Directory.Exists(workDir) ? workDir : "/",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(command);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return $"起不了 bash：{ex.Message}";
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 已经退了
            }

            return $"命令超时（{timeout}s）已杀掉：{Truncate(command, 120, "…")}";
        }

        stdout.Append(await stdoutTask);
        stderr.Append(await stderrTask);

        var text = stdout.ToString();
        if (stderr.Length > 0)
        {
            text += (text.Length > 0 ? "\n" : string.Empty) + "[stderr] " + stderr;
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return $"(命令退出码 {process.ExitCode}，没有输出)";
        }

        return $"退出码 {process.ExitCode}\n" + Truncate(text, 4000, "（输出太长，只给了前 4000 字）");
    }

    private static string? Resolve(string? path, string workDir)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim().Trim('"');
        return Path.IsPathRooted(trimmed) ? Path.GetFullPath(trimmed) : Path.GetFullPath(Path.Combine(workDir, trimmed));
    }

    private static string DescribeTool(string name, string? command, string? arg) => name switch
    {
        "bash" => $"🔧 跑命令 {Shorten((command ?? string.Empty).Replace('\n', ' '), 40)}",
        "read" => $"📖 读文件 {Shorten(arg ?? string.Empty, 40)}",
        "write" => $"📝 写文件 {Shorten(arg ?? string.Empty, 40)}",
        "fetch" => $"🌐 抓网页 {Shorten(arg ?? string.Empty, 40)}",
        _ => $"🔧 {name}"
    };

    private static string Truncate(string text, int max, string note)
        => text.Length <= max ? text : text[..max] + "\n" + note;

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
