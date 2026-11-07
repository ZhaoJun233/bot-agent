using System.Text.Json;
using BotAgent.Domain.Agent;
using BotAgent.Services;
using BotAgent.PythonAgentProbe;

if (args.Length != 1) throw new ArgumentException("Pass the operator-provisioned Python executable path.");
FileLog.WriteToFile = false;
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL " + label);
    Console.WriteLine("PASS " + label); passed++;
}
const string call = """{"tool":"mcp.fixture.MixedEcho"}""";
const string final = """{"final":"synthetic complete"}""";
await using var fixture = new AgentFixture(args[0]);
Check(await fixture.DiscoverAsync() == 1, "real Python worker discovers mixed-case tool");
var pending = fixture.Prepare([AgentFixture.ToolName]);
Check(pending.PythonToolLeases.Count == 1, "admission captures exact immutable source identity");
Check(fixture.Gate.Create(pending) is not null, "local code requires approval even legacy gate disabled");
fixture.Model.Set(call, final);
fixture.Runtime.Submit(pending);
Check(fixture.Runtime.Get(pending.Id)?.State == AgentTaskState.WaitingApproval, "actual runtime waits for approval");
Check(fixture.Effects == 0 && fixture.Model.Requests.Count == 0, "waiting approval has zero model or worker side effects");
await fixture.RunAsync(pending);
if (fixture.Effects != 1)
{
    Console.WriteLine(JsonSerializer.Serialize(fixture.Runtime.Get(pending.Id)));
    Console.WriteLine("modelRequests=" + fixture.Model.Requests.Count);
    Console.WriteLine(string.Join("\n", fixture.Logs));
}
Check(fixture.Effects == 1, "approved actual executor command-service model loop dispatches exactly once");
Check(fixture.Runtime.Get(pending.Id)?.State == AgentTaskState.Succeeded, "approved task completes");
Check(fixture.Model.Requests[0].Contains(AgentFixture.ToolName, StringComparison.Ordinal), "admitted exact-case tool appears in model prompt");

var rejected = fixture.Prepare([AgentFixture.ToolName]);
var requestsBeforeReject = fixture.Model.Requests.Count;
fixture.Model.Set(call, final);
await fixture.RunAsync(rejected, approve: false);
Check(fixture.Effects == 1 && fixture.Model.Requests.Count == requestsBeforeReject, "rejected task has zero model or worker effects");

var stale = fixture.Prepare([AgentFixture.ToolName]);
var persisted = JsonSerializer.Deserialize<AgentTaskRequest>(JsonSerializer.Serialize(stale))!;
var oldToken = persisted.PythonToolLeases.Single().SourceGeneration;
fixture.Runtime.Submit(persisted);
var oldGeneration = fixture.Source.Generation;
await fixture.Source.ReplaceAsync();
await fixture.DiscoverAsync();
Check(oldGeneration == 1 && fixture.Source.Generation == 1, "replacement source restarts numeric generation at one");
Check(fixture.UseCase.Tools.Single().SourceGeneration != oldToken, "opaque owner identity differs across reconstruction");
fixture.Model.Set(call, final);
await fixture.RunAsync(persisted);
Check(fixture.Effects == 1, "persisted old same-name approval cannot authorize reconstructed worker");

fixture.Model.Set(call, final);
await fixture.RunAsync(fixture.Prepare([AgentFixture.ToolName]));
Check(fixture.Effects == 2, "fresh admission after replacement dispatches once");

fixture.Model.Set(call, call, final);
await fixture.RunAsync(fixture.Prepare([AgentFixture.ToolName]));
Check(fixture.Effects == 3, "central call budget rejects second Python side effect");

foreach (var scope in new IReadOnlyList<string>?[] { null, ["read"], ["mcp.fixture.mixedecho"] })
{
    var denied = fixture.Prepare(scope);
    Check(denied.PythonToolLeases.Count == 0, "inherited wrong-scope or wrong-case requests capture no Python lease");
    fixture.Model.Set(call, final);
    await fixture.RunAsync(denied);
    Check(fixture.Effects == 3, "unleased or wrong-scope model-authored tool cannot execute");
}

fixture.Model.Set("""{"tool":"mcp.fixture.MixedEcho","unexpected":true}""", final);
await fixture.RunAsync(fixture.Prepare([AgentFixture.ToolName]));
Check(fixture.Effects == 3, "central schema validation rejects unexpected Python arguments");

var legacy = JsonSerializer.Deserialize<AgentTaskRequest>("""{"Id":"legacy","SourceKey":"panel:synthetic","SessionId":"synthetic-session","Prompt":"synthetic"}""")!;
Check(legacy.PythonToolLeases.Count == 0, "old persisted request without lease has no Python authority");
fixture.Model.Set(call, final);
await fixture.Commands.RunServerAgentDirectAsync("synthetic ordinary call", 15, toolScope: [AgentFixture.ToolName]);
Check(fixture.Effects == 3, "ordinary non-runtime server path cannot invoke local Python");
Console.WriteLine($"PythonAgentProbe: {passed} passed");
