using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Tools;
using BotAgent.Domain.Mcp;
using BotAgent.Services.Mcp;
using BotAgent.Services.Tools;

namespace BotAgent.Services.Agent;

/// <summary>
/// **服务器自己**的 agent：跑在机器人容器里，用模型 + 工具循环干活（管理员 2026-09-17 要的“bot 自己也要有 agent 能力”）。
///
/// 为什么不用“在容器里再装一个 pi”：
///   • 镜像要加 Node + npm + pi（几百 MB），而容器里真正缺的是**干活的脑子**，不是那个 CLI；
///   • pi 的价值在管理员本机的文件/工具链 —— 本机桥已于 cfg-0.3.3 移除，服务器路径不再依赖它；
///   • 容器里的 agent 要干的是“看日志、翻数据、查库存、跑健康检查”这类运维活：bash + 读写文件 + 抓 URL 就够。
/// 所以这里是一个**原生 C# 的工具循环**：同一个模型、同一套白名单，只是执行地点在容器里。
///
/// 工具循环长什么样（故意不用 function-calling，跟机器人其它地方一个口径：模型吐 JSON）：
///   user: 用户那句 //指令（+ 现场信息：容器里有什么工具、工作目录）
///   → 模型回 {"thought":"…","tool":"bash","command":"ls -la /data"}  → 真跑 → 输出喂回去 → 再问
///   → 直到 {"final":"……"} 或步数用完
/// </summary>
public sealed class ServerAgentRunner : BotAgent.Services.Tools.IToolExecutor
{
    // 配置读取入口：指向**当前发布版**（热更新是换引用，见 SettingsBox）——不要改成缓存实例。
    private AppSettings _settings => _box.Current;

    private readonly SettingsBox _box;
    private readonly OpenAiClient _brain;
    private readonly McpToolUseCase? _mcp;
    private readonly Func<string, string, JsonObject, CancellationToken, Task<ToolOutcome?>>? _sameScopePluginTool;
    private readonly Func<string, IReadOnlyList<string>>? _listSameScopePluginTools;

    /// <summary>已发现 MCP 工具名的副本，用于任务受理时冻结权限上限。</summary>
    internal IReadOnlyList<string> GetTaskMcpToolNames()
        => _mcp?.Tools.Select(tool => tool.QualifiedName).ToArray() ?? [];

    internal IReadOnlyList<string> GetTaskReadOnlyMcpToolNames()
        => _mcp?.Tools.Where(t => !t.IsLocalProcess && t.ReadOnlyConfirmed).Select(t => t.QualifiedName).ToArray() ?? [];

    internal IReadOnlyList<string> GetSameScopeReadPluginToolIds(string sourceKey)
        => (_listSameScopePluginTools?.Invoke(sourceKey) ?? [])
            .Select(ExtractPluginToolId)
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<McpToolInfo> CaptureTaskPythonLeases(IEnumerable<string> names)
        => _mcp?.CaptureLocalLeases(names) ?? [];
    private readonly Action<string> _log;

    /// <summary>一轮里最多做几个 QQ 动作（防“给每个人都点一遍”——真去 QQ 里刷一圈比多跑几步麻烦得多）。</summary>
    private const int MaxQqActionsPerTask = ServerAgentPromptBuilder.MaxQqActionsPerTask;

    /// <summary>
    /// 一个任务的总时长上限（秒）。为什么需要：以前只有“最多 N 步”✗，而每步的命令还能各跑 60 秒，
    /// 一个跑偏的任务能磨十几分钟 —— 群里看着就是“卡住了”✗（2026-09-21 管理员报的：
    /// 让它点十个赞，它不调 qq 工具，改去 docker 里翻 NapCat ✗，13 步 30 秒还在找路）。
    /// 到点不是硬杀，而是让它先用已有的信息把结论说出来。
    /// </summary>
    private const int TaskMaxSeconds = 240;

    public ServerAgentRunner(SettingsBox box, OpenAiClient brain, IHttpFetcher http, Action<string> log,
        McpToolUseCase? mcp = null,
        Func<string, string, JsonObject, CancellationToken, Task<ToolOutcome?>>? sameScopePluginTool = null,
        Func<string, IReadOnlyList<string>>? listSameScopePluginTools = null)
    {
        _box = box;
        _brain = brain;
        _http = http;
        _log = log;
        _mcp = mcp;
        _sameScopePluginTool = sameScopePluginTool;
        _listSameScopePluginTools = listSameScopePluginTools;
    }

    /// <summary>出网（服务器 agent 抓页面用）：socket 由装配点持有，见 <see cref="IHttpFetcher" />。</summary>
    private readonly IHttpFetcher _http;

    /// <summary>进度（工具调用）—— 与桥同一套事件，BotAgentHost 那边不用分叉。</summary>
    public event Action<AgentTask>? Progress;

    // ── 批次 A 收尾：这一族**真的能执行**（IToolExecutor）──
    // 登记表里它是 `server.agent`；执行体就是既有那个 switch（RunToolAsync），这里只把
    // “一次 ToolCall” 翻译成它的入参。**判定不在这里**：白名单与闸门都在 RunAsync 那条路上，
    // 这个入口按同一口径自己解析一次设置（谁调用它，谁负责先过闸门）。

    /// <summary>执行者标识（与 <c>ServerToolSpecs.ExecutorAgent</c> 同一个常量）。</summary>
    public string Id => BotAgent.Services.Tools.ServerToolSpecs.ExecutorAgent;

    /// <summary>今天真正干这件事的组件（面板与审计展示用）。</summary>
    public string Implementation => "Services/Agent/ServerAgentRunner.cs（本体就是那个 switch）";

    /// <summary>false = 已经接进统一执行。</summary>
    public bool LegacyPath => false;

    /// <summary>
    /// 统一执行入口：只接**服务器工具**（bash/read/write/fetch/docker）。
    /// `qq` 不在这里 —— 它由会话宿主（<c>SessionQqActionHost</c>，见 QqActionTool.cs）执行，
    /// 因为 QQ 动作要 <c>sender</c>/<c>me</c>/<c>this</c> 这类**会话现场**才能翻成真数字。
    /// </summary>
    public async Task<BotAgent.Domain.Tools.ToolOutcome> ExecuteAsync(
        BotAgent.Domain.Tools.ToolCall call, CancellationToken ct = default)
    {
        var tool = call?.ToolId ?? string.Empty;
        if (string.Equals(tool, "qq", StringComparison.Ordinal))
        {
            return BotAgent.Domain.Tools.ToolOutcome.Failure(
                "wrong_executor", "QQ 动作由会话宿主执行（要会话现场才能解析 sender/me/this）。");
        }

        if (_mcp?.IsMcpTool(tool) == true)
        {
            return BotAgent.Domain.Tools.ToolOutcome.Failure(
                "no_conversation", "MCP 工具只能在 // 会话路径执行（需要会话 key 做审计与预算）。");
        }

        var workDir = string.IsNullOrWhiteSpace(_settings.AgentServerWorkDir) ? "/data" : _settings.AgentServerWorkDir.Trim();
        // #136：白名单只从单入口算一次（与 RunAsync 同一份口径）。
        var access = ServerToolAccess.Resolve(_settings, DockerAvailability.IsAvailable);
        var allowed = access.Tools;

        if (!allowed.Contains(tool))
        {
            return BotAgent.Domain.Tools.ToolOutcome.Failure(
                "not_allowlisted", $"工具 {tool} 没开（当前只允许：{string.Join(", ", allowed.OrderBy(x => x))}）");
        }

        var step = new StepCall(tool, call!.Arguments?["path"]?.GetValue<string>()
            ?? call.Arguments?["url"]?.GetValue<string>() ?? call.Arguments?["file"]?.GetValue<string>(),
            call.Arguments?["command"]?.GetValue<string>(), null, call.Arguments ?? new JsonObject());

        var output = await RunToolAsync(tool, step, workDir, allowed,
            conversationKey: string.Empty, callIndex: call.CallIndex, ct);

        return BotAgent.Domain.Tools.ToolOutcome.Success(output, $"服务器工具 {tool}");
    }

    /// <summary>跑一个任务（把结果写回 task）。调用方负责在后台线程里跑它。</summary>
    public async Task RunAsync(AgentTask task, CancellationToken ct)
    {
        var workDir = string.IsNullOrWhiteSpace(_settings.AgentServerWorkDir) ? "/data" : _settings.AgentServerWorkDir.Trim();
        var maxSteps = Math.Clamp(_settings.AgentServerMaxSteps, 1, 30);
        // docker 是高权限能力（docker.sock ≈ root）：两把锁同时开才进白名单。
        // ② “容器里真的有 docker”—— compose 默认不挂载 socket（#131）。
        // 工具可见性单入口（#136）：作用域只能收窄（与配置白名单取交集），未知名字静默忽略。
        var access = ServerToolAccess.Resolve(_settings, DockerAvailability.IsAvailable, task.ToolScope);
        var allowed = access.Tools;
        var qqAllowed = access.QqActions;

        // 批次 A 第 2 步的收尾：**可选**过统一闸门（默认关 = 与今天逐字一致，见 AppSettings.AgentServerUseGate）。
        // 开着的判定口径 = 本次允许的工具白名单；高风险那几只由服务端显式点名（ServerToolGate）。
        // 白名单**就是** allowed 本身（不许在这里偷偷加东西：qq 能不能用由 allowed 决定，
        // 与系统提示里 `qqOn = allowed.Contains("qq") && qqHost is not null` 同一口径 ——
        // 之前这里无条件把 qq 塞进去，于是“白名单里没有 qq”时闸门反而放行、由执行层兜底拒掉，
        // 闸门看起来像个摆设（S38 第 ⑩ 步就是这么暴露的）。
        var gatePolicy = _settings.AgentServerUseGate
            ? ServerToolGate.BuildPolicy(allowed)
            : null;
        var qqHost = task.QqHost;
        var qqActions = new QqActionRunState();

        var memoryPrompt = BotAgent.Services.Reply.UnifiedMemoryTurn.Current?.ReadPrompt(task.Prompt);
        var memoryRestricted = BotAgent.Services.Memory.MemoryUseScope.HasMemory;
        if (memoryRestricted) task.MemoryDerived = true;
        var allowSameScopePlugin = memoryRestricted
            && _sameScopePluginTool is not null
            && BotAgent.Services.Memory.MemoryUseScope.AllowsEgress(BotAgent.Domain.Memory.ToolEgressClass.SameScopeRead);
        if (!memoryRestricted) await RefreshMcpAsync(ct);
        var effectiveAccess = memoryRestricted
            ? access.WithoutBuiltinTools()
            : access;
        var system = AppendMemoryTaskNotice(
            BuildTaskSystemPrompt(task, effectiveAccess, workDir), memoryRestricted, allowSameScopePlugin, memoryPrompt);

        var messages = BuildTurnMessages(task);
        var started = Clock.Now;
        var nudged = 0;

        try
        {
            var startedAt = Clock.Now;
            for (var step = 1; step <= maxSteps; step++)
            {
                if (ct.IsCancellationRequested || task.CancelRequested)
                {
                    task.Fail("已取消");
                    return;
                }

                // 总时长上限：到点就别再一条条试了（见 TaskMaxSeconds 的注释）
                var spent = Clock.Now - startedAt;
                if (spent > TimeSpan.FromSeconds(TaskMaxSeconds))
                {
                    task.Fail($"跑了 {spent.TotalSeconds:F0} 秒还没收敛，先停下来（第 {step} 步）"
                              + "——把已经查到的东西说清楚比继续翻更值钱。要接着做就再发一条指令。");
                    return;
                }

                if (memoryRestricted && !BotAgent.Services.Memory.MemoryUseScope.CanUse)
                {
                    task.Fail("记忆授权已失效或被撤销，任务已中止。");
                    return;
                }

                var raw = await _brain.CompleteChatWithReasoningAsync(
                    _settings.AgentServerModel, system, messages, _settings.AgentServerMaxTokens, 0.3,
                    _settings.AgentReasoningEffort, ct,
                    baseUrlOverride: _settings.AgentServerBaseUrl, apiKeyOverride: _settings.AgentServerApiKey);

                if (string.IsNullOrWhiteSpace(raw))
                {
                    task.Fail("模型没有返回内容（看日志的 Agent 行）");
                    return;
                }

                var call = ParseStep(raw);
                if (call is null)
                {
                    // 不是 JSON：先给一次机会，让它把话说进协议里（模型偶尔会拿散文答话，
                    // 而散文一旦被当成结论，工具就一次都不会调 —— 管理员会看到一份“凭空编的答案”）。
                    if (nudged == 0 && (allowed.Count > 0 || _mcp is { ToolCount: > 0 }))
                    {
                        nudged++;
                        messages.Add(("assistant", raw.Trim()));
                        messages.Add(("user",
                            "你上面那句不是 JSON。请只输出一行 JSON：要干活就写带 tool 的那一行（真的去调工具），要回话就写带 final 的那一行。不要用散文回答。"));
                        continue;
                    }

                    messages.Add(("assistant", raw.Trim()));
                    task.Succeeded(qqActions.AppendTo(raw.Trim()));
                    return;
                }

                var step0 = call.Value;
                if (step0.Final is { Length: > 0 })
                {
                    task.ToolCalls = step - 1;
                    // 结论也要进对话：否则下一轮上下文里只剩下“用户问了什么 + 一堆工具输出”，
                    // 模型看不到自己上轮得出了什么（会话记忆缺一半，标题综结也拿不到结论）。
                    messages.Add(("assistant", step0.Final.Trim()));
                    task.Succeeded(qqActions.AppendTo(step0.Final.Trim()));
                    return;
                }

                if (step0.Tool is null)
                {
                    messages.Add(("assistant", raw.Trim()));
                    task.Succeeded(qqActions.AppendTo(raw.Trim()));
                    return;
                }

                if (memoryRestricted)
                {
                    if (!await RunMemoryRestrictedToolStepAsync(
                            task, step, step0, raw, allowSameScopePlugin, access, messages, ct).ConfigureAwait(false))
                        return;
                    continue;
                }

                var name = step0.Tool;
                task.LastNote = _mcp?.IsMcpTool(name) == true ? "MCP " + name : ServerAgentPromptBuilder.DescribeTool(name, step0.Command, step0.Arg, step0.Raw);
                task.ToolCalls = step;
                Progress?.Invoke(task);

                var (output, qqOutcome) = await ExecuteAllowedToolStepAsync(
                    task, step, step0, name, workDir, allowed, qqAllowed, qqActions, gatePolicy, access, qqHost, ct)
                    .ConfigureAwait(false);

                qqActions.Record(qqOutcome);
                LogToolOutput(step, task.LastNote, name, output, qqOutcome);

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
            // 把本轮的对话（含工具步骤）交给上层存进会话：下一轮同一会话能接上
            task.Conversation = messages;

            // 面板/群里都要显示“跑了多久”，服务器这条路不能被落下
            if (task.DurationMs == 0)
            {
                task.DurationMs = (long)(Clock.Now - started).TotalMilliseconds;
            }
        }
    }

    private static string AppendMemoryTaskNotice(
        string system, bool memoryRestricted, bool allowSameScopePlugin, string? memoryPrompt)
    {
        if (!memoryRestricted) return system;
        return system + "\n" + memoryPrompt + (allowSameScopePlugin
            ? "\n本任务使用 L1/L2 受控记忆，仅允许调用同源只读插件工具（如 chat.digest / doc.read / kb.search / kb.list），严禁外发、写文件或调用系统/QQ工具。"
            : "\n本任务使用受控记忆，只能直接回答；不要调用任何工具、外发、写文件或产生持久化派生内容。");
    }

    private async Task<bool> RunMemoryRestrictedToolStepAsync(
        AgentTask task,
        int step,
        StepCall step0,
        string raw,
        bool allowSameScopePlugin,
        ServerToolAccess access,
        List<(string Role, string Content)> messages,
        CancellationToken ct)
    {
        var toolId = step0.Tool!;
        var isSameScope = allowSameScopePlugin
            && access.Scope.Allows(toolId)
            && BotAgent.Domain.Memory.MemoryTaintPolicy.ClassifyTool(toolId, readOnly: true)
                == BotAgent.Domain.Memory.ToolEgressClass.SameScopeRead;
        if (!isSameScope || !BotAgent.Services.Memory.MemoryUseScope.CanUse)
        {
            task.Fail("受控记忆任务不能调用外部工具或未授权工具；请另开不使用记忆的任务。");
            return false;
        }

        var pluginArgs = new JsonObject();
        foreach (var kv in step0.Raw)
        {
            if (kv.Key is "thought" or "tool" or "name" or "args" or "arguments") continue;
            if (kv.Value is JsonValue jv) pluginArgs[kv.Key] = jv.ToString();
        }
        UnpackNestedPluginArgs(step0.Raw["args"] as JsonObject, pluginArgs);
        UnpackNestedPluginArgs(step0.Raw["arguments"] as JsonObject, pluginArgs);

        var pluginOutcome = await _sameScopePluginTool!(task.SourceKey, toolId, pluginArgs, ct).ConfigureAwait(false);
        if (!BotAgent.Services.Memory.MemoryUseScope.CanUse)
        {
            task.Fail("记忆授权已失效或被撤销，任务已中止。");
            return false;
        }
        if (pluginOutcome is null)
        {
            task.Fail("受控记忆任务不能调用外部工具；请另开不使用记忆的任务。");
            return false;
        }
        var pluginOut = pluginOutcome.Ok ? (pluginOutcome.ModelText ?? string.Empty) : $"工具执行拒绝：{pluginOutcome.ReasonCode}";
        task.ToolCalls = step;
        task.LastNote = "同源只读 " + toolId;
        Progress?.Invoke(task);
        messages.Add(("assistant", raw));
        messages.Add(("user", $"工具输出（{toolId}）：\n{pluginOut}\n\n（继续：要么给出 final）"));
        return true;
    }

    private static void UnpackNestedPluginArgs(JsonObject? nested, JsonObject target)
    {
        if (nested is null) return;
        foreach (var kv in nested)
        {
            if (kv.Value is JsonValue jv && !target.ContainsKey(kv.Key))
                target[kv.Key] = jv.ToString();
        }
    }

    private async Task<(string Output, ToolOutcome? QqOutcome)> ExecuteAllowedToolStepAsync(
        AgentTask task,
        int step,
        StepCall step0,
        string name,
        string workDir,
        HashSet<string> allowed,
        HashSet<string> qqAllowed,
        QqActionRunState qqActions,
        BotAgent.Domain.Permissions.ToolPolicy? gatePolicy,
        ServerToolAccess access,
        IQqActionHost? qqHost,
        CancellationToken ct)
    {
        string output;
        ToolOutcome? qqOutcome = null;
        try
        {
            if (_mcp?.IsMcpTool(name) == true && !access.AllowsMcp(name))
            {
                output = $"工具 {name} 不在这次任务的工具作用域里，不能调用。"
                         + "用作用域里允许的工具，或者直接给 final 说明你查到了什么。";
            }
            else if (_mcp?.IsMcpTool(name) == true)
                output = await RunToolAsync(name, step0, workDir, allowed, task.SourceKey, step - 1, ct,
                    task.PythonToolLeases.FirstOrDefault(tool => tool.QualifiedName == name));
            else if (name == "qq" && ++qqActions.Count > MaxQqActionsPerTask)
            {
                output = $"这次已经做了 {MaxQqActionsPerTask} 个 QQ 动作，不再做了（要接着做请再发一条指令）。";
                qqOutcome = ToolOutcome.Failure("action_budget_exceeded", output);
            }
            else if (_sameScopePluginTool is not null
                     && BotAgent.Domain.Memory.MemoryTaintPolicy.ClassifyTool(name, readOnly: true)
                         == BotAgent.Domain.Memory.ToolEgressClass.SameScopeRead
                     && !allowed.Contains(name))
            {
                if (!access.Scope.Allows(name))
                {
                    output = $"工具 {name} 未在本次允许的作用域内。";
                }
                else
                {
                    var pluginArgs = new JsonObject();
                    foreach (var kv in step0.Raw)
                    {
                        if (kv.Key is "thought" or "tool" or "name" or "args" or "arguments") continue;
                        if (kv.Value is JsonValue jv) pluginArgs[kv.Key] = jv.ToString();
                    }
                    UnpackNestedPluginArgs(step0.Raw["args"] as JsonObject, pluginArgs);
                    UnpackNestedPluginArgs(step0.Raw["arguments"] as JsonObject, pluginArgs);
                    var pluginOutcome = await _sameScopePluginTool(task.SourceKey, name, pluginArgs, ct).ConfigureAwait(false);
                    output = pluginOutcome is null
                        ? $"同源只读工具 {name} 不可用或未在当前会话启用。"
                        : pluginOutcome.Ok
                            ? (pluginOutcome.ModelText ?? "（已完成，没有文本输出）")
                            : $"同源只读工具执行失败（{pluginOutcome.ReasonCode}）";
                }
            }
            else if (gatePolicy is not null
                     && ServerToolGate.Check(gatePolicy, task.SourceKey, name) is { Allow: false } gateDenied)
            {
                _log($"[ServerAgent] 闸门拒绝 {name}（{gateDenied.ReasonCode}）→ 不执行");
                output = $"这一步被闸门拒绝了（{gateDenied.ReasonCode}）：这次不执行。"
                         + "换个做法，或者直接给 final 说清楚你查到了什么。";
                if (name == "qq") qqOutcome = ToolOutcome.Failure("tool_denied", output, gateDenied.ReasonCode);
            }
            else if (name == "qq")
            {
                qqOutcome = qqActions.Uncertain
                    ? ToolOutcome.Failure("previous_outcome_unknown", "前一个 QQ 动作结果未确认，本任务停止后续 QQ 动作；不要重试。")
                    : await RunQqToolAsync(step0.Raw, allowed, qqAllowed, qqHost, step - 1, ct);
                output = qqOutcome.ModelText ?? qqOutcome.ReasonCode;
            }
            else
            {
                output = await RunToolAsync(name, step0, workDir, allowed, task.SourceKey, step - 1, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (name == "qq")
            {
                output = $"动作结果未确认（{ex.GetType().Name}）；不要自动重试。";
                qqOutcome = ToolOutcome.Failure("delivery_unknown", output);
            }
            else output = $"工具执行出错：{ex.GetType().Name} {ex.Message}";
        }
        return (output, qqOutcome);
    }

    /// <summary>Build task instructions with only the local tool identities admitted before approval.</summary>
    private string BuildTaskSystemPrompt(AgentTask task, ServerToolAccess access, string workDir)
    {
        // The model may see only local tools captured before approval, never freshly discovered authority.
        var mcpNames = (_mcp?.Tools ?? []).Where(tool => access.AllowsMcp(tool.QualifiedName)
            && (!tool.IsLocalProcess || task.PythonToolLeases.Contains(tool)))
            .Select(tool => tool.QualifiedName).ToArray();
        var mcpScope = ToolScope.FromIds(mcpNames.Length > 0 ? mcpNames : ["__deny_all_mcp__"]);
        var pluginTools = (_listSameScopePluginTools?.Invoke(task.SourceKey) ?? [])
            .Where(item => access.Scope.Allows(ExtractPluginToolId(item)))
            .ToArray();
        var system = ServerAgentPromptBuilder.BuildSystemPrompt(
            _settings, _mcp, workDir, access.Tools, access.QqActions, task.QqHost, mcpScope, pluginTools)
            + (task.Natural ? ServerAgentPromptBuilder.NaturalTone : string.Empty);

        return system;
    }

    private static string ExtractPluginToolId(string descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor)) return string.Empty;
        var paren = descriptor.IndexOf('(');
        return (paren > 0 ? descriptor[..paren] : descriptor).Trim();
    }

    private static bool IsProhibitedQqActionOutcome(ToolOutcome outcome)
        => outcome.ReasonCode is "action_not_allowed" or "tool_denied" or "plugin_denied"
            or "platform_mismatch" or "platform_unavailable" or "capability_denied" or "source_retired"
            or "invalid_arguments" or "unknown_action" or "context_missing" or "structured_executor_missing" or "platform_action_unsupported"
            or "action_budget_exceeded" or "previous_outcome_unknown";

    private sealed class QqActionRunState
    {
        private readonly List<string> _denials = [];
        private readonly List<string> _failures = [];
        public int Count { get; set; }
        public bool Uncertain { get; private set; }
        public void Record(ToolOutcome? outcome)
        {
            if (outcome is not { Ok: false }) return;
            Uncertain |= outcome.ReasonCode == "delivery_unknown";
            var entries = IsProhibitedQqActionOutcome(outcome) ? _denials : _failures;
            entries.Add((outcome.ModelText ?? outcome.ReasonCode).Trim());
        }
        public string AppendTo(string final) => AppendQqActionOutcomes(final, _denials, _failures);
    }

    private void LogToolOutput(int step, string? note, string name, string output, ToolOutcome? qqOutcome)
        => _log(qqOutcome is not null
            ? $"[ServerAgent] 第 {step} 步 QQ ok={qqOutcome.Ok} reason={qqOutcome.ReasonCode}"
            : $"[ServerAgent] 第 {step} 步 {note} → " + (_mcp?.IsMcpTool(name) == true
                ? "MCP（内容不回显）" : Shorten(output.Replace('\n', ' '), 110)));

    private static string AppendQqActionOutcomes(string final, IReadOnlyCollection<string> denials,
        IReadOnlyCollection<string> failures)
    {
        var denied = denials.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        var failed = failures.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (denied.Length > 0)
        {
            final += "\n\n未执行的 QQ 动作：\n" + string.Join("\n", denied);
        }
        if (failed.Length > 0)
        {
            final += "\n\n执行未成功的 QQ 动作：\n" + string.Join("\n", failed);
        }
        return final;
    }


    private static List<(string Role, string Text)> BuildTurnMessages(AgentTask task)
    {
        // Session history stays independent from ephemeral memory; keep only conversational turns.
        var messages = new List<(string Role, string Text)>();
        if (task.UseHistory && task.History is { Count: > 0 }) messages.AddRange(SeedHistory(task.History));
        messages.Add(("user", task.Prompt));
        return messages;
    }

    /// <summary>
    /// 为什么：工具步骤长这样 —— <c>{"tool":"bash","command":"uptime…"}</c> 与 <c>工具输出（bash）：…</c>。
    /// 它们对“这个会话在聊什么”几乎没贡献，却把模型带进“我正在跑命令”的模式：
    /// 管理员实测过一次 —— 上一轮查服务器状态留下的 8 条工具步骤，让模型对新的“给某某点赞”
    /// 一个工具都不调、直接续写了一份状态汇报。
    /// </summary>
    private static List<(string Role, string Text)> SeedHistory(List<(string Role, string Text)> history)
    {
        var kept = new List<(string Role, string Text)>();
        foreach (var (role, text) in history)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 工具输出那一条（runner 自己拼的）
            if (role == "user" && text.StartsWith("工具输出（", StringComparison.Ordinal))
            {
                continue;
            }

            // 工具调用那一条（模型吐的 JSON 里带 tool）
            if (role == "assistant" && LooksLikeToolStep(text))
            {
                continue;
            }

            kept.Add((role, text));
        }

        // 再老的对话留着只会吃 token、还容易误导（会话内存本来就只当“最近聊过什么”用）
        const int maxEntries = 12;
        return kept.Count <= maxEntries ? kept : kept[^maxEntries..];
    }

    /// <summary>这段助手回复是不是“调工具”的那一行（而不是结论）。</summary>
    private static bool LooksLikeToolStep(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            return JsonNode.Parse(trimmed[start..(end + 1)]) is JsonObject obj && obj["tool"] is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// `//` 这条路的工具白名单（#131）：docker 要**两把锁同时开**才留 ——
    /// 面板开关 <see cref="AppSettings.AgentServerDocker" /> 打开，且这个容器里真的有 docker。
    ///
    /// 为什么加第二把：compose 默认不挂载 docker socket（见 SECURITY.md「部署方的最低要求」与 issue #131）；
    /// 以前只看开关，于是开关一打开提示词里就列出 docker，模型照着去用，每次都注定失败。
    /// 白名单与系统提示同源（<c>BuildSystemPrompt</c> 只描述 <c>allowed</c> 里的工具），摘掉就够。
    /// </summary>
    public static HashSet<string> ResolveAllowedTools(AppSettings settings, bool dockerAvailable)
        // #136：实现搬到 <see cref="ServerToolAccess.ConfiguredTools" />（同一条路的单入口），
        // 这里保留旧签名只为既有探针不用改。
        => ServerToolAccess.ConfiguredTools(settings, dockerAvailable);

    /// <summary>模型这一轮给出的东西（原样留着——qq 工具要读 user_id/times 这些自定义字段）。</summary>
    private readonly record struct StepCall(string? Tool, string? Arg, string? Command, string? Final, JsonObject Raw);

    /// <summary>解析模型这一轮的 JSON（宽容：外面带解释、带代码围栏都认）。</summary>
    private static StepCall? ParseStep(string raw)
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

        var tool = obj["tool"]?.GetValue<string>()?.Trim() ?? obj["name"]?.GetValue<string>()?.Trim();
        // Dynamic names are case-sensitive identifiers; only legacy builtin commands normalize case.
        if (tool is not null && !tool.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase))
            tool = tool.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(tool))
        {
            var final = obj["final"]?.GetValue<string>() ?? obj["answer"]?.GetValue<string>() ?? obj["reply"]?.GetValue<string>();
            return new StepCall(null, null, null, final, obj);
        }

        var arg = obj["path"]?.GetValue<string>() ?? obj["url"]?.GetValue<string>() ?? obj["file"]?.GetValue<string>();
        var command = obj["command"]?.GetValue<string>() ?? obj["cmd"]?.GetValue<string>() ?? obj["content"]?.GetValue<string>();
        var finalText = obj["final"]?.GetValue<string>();
        return new StepCall(tool, arg, command, finalText, obj);
    }

    private static async Task<ToolOutcome> RunQqToolAsync(JsonObject arguments, HashSet<string> allowed,
        HashSet<string> qqAllowed, IQqActionHost? host, int callIndex, CancellationToken ct)
    {
        if (!allowed.Contains("qq")) return ToolOutcome.Failure("tool_denied", "qq 工具本次未启用。");
        if (host is null) return ToolOutcome.Failure("context_missing", "qq 工具这次没有会话上下文（不在 QQ 会话里跑），用不了。");
        var asked = arguments["action"] is JsonValue value && value.TryGetValue<string>(out var name) ? name.Trim() : null;
        var spec = QqActionCatalog.Find(asked);
        if (spec is null) return ToolOutcome.Failure("unknown_action", $"不认识的动作（本次允许：{string.Join(", ", qqAllowed)}）");
        if (!qqAllowed.Contains(spec.Name))
            return ToolOutcome.Failure("action_not_allowed", $"动作 {spec.Name} 没开（本次允许：{string.Join(", ", qqAllowed)}）。"
                + "如果确实要做，让管理员在面板「服务器 agent 的 QQ 动作」里把它写上。");
        if (host is not IToolExecutor executor)
            return ToolOutcome.Failure("structured_executor_missing", "动作宿主未接入结构化执行，本次动作未执行。");
        return await executor.ExecuteAsync(new ToolCall("qq." + spec.Name, arguments, callIndex), ct);
    }

    private async Task<string> RunToolAsync(string tool, StepCall call, string workDir, HashSet<string> allowed,
        string conversationKey, int callIndex,
        CancellationToken ct, McpToolInfo? pythonLease = null)
    {
        if (_mcp?.IsMcpTool(tool) == true)
        {
            if (string.IsNullOrWhiteSpace(conversationKey))
            {
                return "MCP 工具需要会话上下文（// 路径）";
            }

            var outcome = pythonLease is null
                ? await _mcp.ExecuteAsync(conversationKey, tool, McpArguments(call.Raw), callIndex, ct)
                : await _mcp.ExecuteWithLeaseAsync(conversationKey, pythonLease, McpArguments(call.Raw), callIndex, ct);
            return outcome.ModelText
                   ?? (outcome.Ok ? "（MCP 工具已完成，没有文本输出）" : $"MCP 工具失败（{outcome.ReasonCode}）");
        }

        var arg = call.Arg;
        var command = call.Command;

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

                using var response = await _http.GetAsync(uri, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                return $"HTTP {(int)response.StatusCode}\n" + Truncate(body, 4000, "（正文太长，只给了前 4000 字）");
            }

            case "docker":
            {
                if (string.IsNullOrWhiteSpace(command))
                {
                    return "缺少 command 字段（写 docker 后面那段，例如 ps -a）";
                }

                // 高权限动作：日志留一条（出了事能对号入座）
                _log($"[ServerAgent] docker {Shorten(command!, 120)}");
                return await RunShellAsync("docker " + command!, workDir, ct);
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

        var result = await BoundedShellProcess.RunAsync(psi, TimeSpan.FromSeconds(timeout), ct);
        if (result.Failure is { } failure)
        {
            var truncation = result.Truncated ? "（此前输出已截断，未保留超出部分）" : string.Empty;
            return (failure == "command_timeout"
                ? $"命令超时（{timeout}s），已请求终止进程树。"
                : $"命令失败（{failure}），已请求终止进程树。") + truncation;
        }

        var text = result.Stdout;
        if (result.Stderr.Length > 0)
            text += (text.Length > 0 ? "\n" : string.Empty) + "[stderr] " + result.Stderr;
        text = text.Trim();
        if (text.Length == 0) return $"(命令退出码 {result.ExitCode}，没有输出)";
        var truncated = result.Truncated || text.Length > 4000;
        var prefix = text.Length > 4000 ? text[..4000] : text;
        return $"退出码 {result.ExitCode}\n" + prefix +
            (truncated ? "\n（输出已截断；仅保留前 4000 字，超出部分未保存在内存中）" : string.Empty);
    }

    private async Task RefreshMcpAsync(CancellationToken ct)
    {
        if (_mcp is not { Enabled: true })
        {
            return;
        }

        try
        {
            await _mcp.RefreshAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log($"[ServerAgent] MCP 发现失败（{ex.GetType().Name}）");
        }
    }

    /// <summary>MCP 参数只去掉协议信封字段；url/path/command 等同名字段可能是工具自己的参数，保留。</summary>
    private static JsonObject McpArguments(JsonObject raw)
    {
        var args = (JsonObject)raw.DeepClone();
        foreach (var key in new[] { "tool", "name", "thought", "final", "answer", "reply" })
        {
            args.Remove(key);
        }

        return args;
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

    private static string Truncate(string text, int max, string note)
        => text.Length <= max ? text : text[..max] + "\n" + note;

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
