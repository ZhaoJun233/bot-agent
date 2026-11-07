using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Adapters.Net;
using BotAgent.Domain.Agent;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Ports;
using BotAgent.Engine.Runtime;
using BotAgent.Mcp;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Mcp;

namespace BotAgent.PythonAgentProbe;

/// <summary>Synthetic dependencies around the actual product runtime/executor/command-service path.</summary>
public sealed class AgentFixture : IAsyncDisposable
{
    public const string ToolName = "mcp.fixture.MixedEcho";
    public string Root { get; }
    public string Marker { get; }
    public LeasedWorker Source { get; }
    public McpToolUseCase UseCase { get; }
    public ScriptedModel Model { get; } = new();
    public List<string> Logs { get; } = [];
    public AgentCommandService Commands { get; }
    public AgentTaskRuntime Runtime { get; }
    public IAgentTaskApprovalGate Gate { get; }
    public int Effects => File.Exists(Marker) ? File.ReadAllLines(Marker).Length : 0;
    private readonly MethodInfo _prepare;
    private int _ids;

    public AgentFixture(string python)
    {
        Root = Path.Combine(AppContext.BaseDirectory, "synthetic-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        foreach (var file in new[] { "worker.py", "botagent_worker.py" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(Root, file));
        Marker = Path.Combine(Root, "effects.txt");
        Source = new LeasedWorker(python, Root, Marker);
        UseCase = new McpToolUseCase(Source, maxCallsPerRun: 1);
        var settings = new AppSettings
        {
            ApiKey = "synthetic-key", Model = "synthetic-model", ModelBaseUrl = "https://example.com/v1",
            AgentServerModel = "synthetic-model", AgentServerTools = "read", AgentServerQqActions = "", AgentServerMaxSteps = 5,
            AgentServerWorkDir = Root, AgentProgressSeconds = 0,
        };
        var box = new SettingsBox(settings);
        var brain = new OpenAiClient(box, Model, Model, null!, null!);
        var runner = new ServerAgentRunner(box, brain, Model, Logs.Add, UseCase);
        var sessions = DispatchProxy.Create<IAgentSessionStore, SessionProxy>();
        Commands = new AgentCommandService(box, null!, brain, null!, null!, sessions, runner, null!,
            new AgentHooks(_ => { }, () => 0, _ => false, (_, _) => Task.CompletedTask));
        var assembly = typeof(AgentCommandService).Assembly;
        var gateType = assembly.GetType("BotAgent.Adapters.Agent.TaskApprovalGate", true)!;
        Gate = (IAgentTaskApprovalGate)Activator.CreateInstance(gateType,
            [new Func<DateTimeOffset>(() => DateTimeOffset.UtcNow), new Func<bool>(() => false),
             new Func<AppSettings>(() => settings), new Func<bool>(() => false),
             new Func<IEnumerable<string>, IReadOnlyList<McpToolInfo>>(UseCase.CaptureLocalLeases)])!;
        _prepare = gateType.GetMethod("Prepare")!;
        var executorType = assembly.GetType("BotAgent.Adapters.Agent.AgentTaskExecutor", true)!;
        var executor = (IAgentTaskExecutor)Activator.CreateInstance(executorType, [Commands])!;
        Runtime = new AgentTaskRuntime(executor, () => DateTimeOffset.UtcNow, log: Logs.Add, approvalGate: Gate);
    }

    public Task<int> DiscoverAsync() => UseCase.RefreshAsync(CancellationToken.None);

    public AgentTaskRequest Prepare(IReadOnlyList<string>? scope = null)
    {
        var request = new AgentTaskRequest
        {
            Id = "synthetic-" + Interlocked.Increment(ref _ids), SourceKey = "panel:synthetic", SessionId = "synthetic-session",
            Prompt = "Execute the explicitly scoped synthetic tool then finish.", ToolScope = scope,
            Timeout = TimeSpan.FromSeconds(15), CaptureResult = true,
        };
        return (AgentTaskRequest)_prepare.Invoke(Gate, [request, UseCase.Tools.Select(t => t.QualifiedName).ToArray()])!;
    }

    public async Task RunAsync(AgentTaskRequest request, bool approve = true)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(AgentTaskSnapshot snapshot)
        {
            if (snapshot.Id == request.Id && snapshot.State is AgentTaskState.Succeeded or AgentTaskState.Failed
                or AgentTaskState.Cancelled or AgentTaskState.TimedOut) finished.TrySetResult();
        }
        Runtime.Changed += Changed;
        try
        {
            if (Runtime.Get(request.Id) is null) Runtime.Submit(request);
            if (Runtime.Get(request.Id)?.State == AgentTaskState.WaitingApproval)
                Runtime.DecideApproval(Runtime.Get(request.Id)!.ApprovalRequestId!, approve, DateTimeOffset.UtcNow);
            if (Runtime.Get(request.Id)?.State is AgentTaskState.Succeeded or AgentTaskState.Failed
                or AgentTaskState.Cancelled or AgentTaskState.TimedOut) finished.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally { Runtime.Changed -= Changed; }
    }

    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        await Source.DisposeAsync();
        Directory.Delete(Root, true);
    }
}

/// <summary>Probe-only owner wrapper; host broker has separate package/incarnation tests.</summary>
public sealed class LeasedWorker(string python, string root, string marker) : IMcpToolSource
{
    private PythonWorkerToolSource _worker = Create(python, root, marker);
    private string _owner = Guid.NewGuid().ToString("N");
    public long Generation => _worker.Generation;
    public bool Enabled => _worker.Enabled;
    public McpConnectionState State => _worker.State;
    private static PythonWorkerToolSource Create(string python, string root, string marker)
        => new(new PythonWorkerOptions("fixture", python, root, "worker.py", ["MixedEcho"], TimeSpan.FromSeconds(3), 4096)
        { Configuration = new JsonObject { ["marker"] = marker } });
    public async Task<IReadOnlyList<McpToolInfo>> DiscoverAsync(CancellationToken ct)
        => (await _worker.DiscoverAsync(ct)).Select(tool => tool with
        { SourceGeneration = _owner + ":" + tool.SourceGeneration }).ToArray();
    public bool IsToolCurrent(McpToolInfo tool)
        => tool.QualifiedName == AgentFixture.ToolName && tool.SourceGeneration == _owner + ":" + Generation
            && State == McpConnectionState.Ready;
    public Task<McpCallResult> CallWithLeaseAsync(McpToolInfo tool, JsonObject arguments, CancellationToken ct)
        => IsToolCurrent(tool)
            ? _worker.CallWithGenerationAsync(tool.QualifiedName, arguments,
                long.Parse(tool.SourceGeneration!.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture), ct)
            : Task.FromResult(new McpCallResult(false, "mcp_source_changed", null));
    public Task<McpCallResult> CallAsync(string name, JsonObject arguments, CancellationToken ct)
        => Task.FromResult(new McpCallResult(false, "mcp_local_approval_required", null));
    public async Task ReplaceAsync()
    {
        await _worker.DisposeAsync();
        _owner = Guid.NewGuid().ToString("N");
        _worker = Create(python, root, marker);
    }
    public ValueTask DisposeAsync() => _worker.DisposeAsync();
}

public class SessionProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "Find" => new AgentSession { Id = "synthetic-session", Name = "synthetic", Backend = "server" },
        "StartRun" => "synthetic-run",
        "FinishRun" => null,
        _ => throw new InvalidOperationException("Unexpected synthetic session operation: " + method.Name),
    };
}

public sealed class ScriptedModel : IHttpFetcher
{
    private readonly Queue<string> _replies = new();
    public List<string> Requests { get; } = [];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public void Set(params string[] replies) { _replies.Clear(); foreach (var reply in replies) _replies.Enqueue(reply); }
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        Requests.Add(await request.Content!.ReadAsStringAsync(ct));
        var reply = _replies.Count > 0 ? _replies.Dequeue() : """{"final":"synthetic complete"}""";
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
        { choices = new[] { new { message = new { content = reply } } } })) };
    }
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption option, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> GetStringAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default)
        => SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = content }, ct);
}
