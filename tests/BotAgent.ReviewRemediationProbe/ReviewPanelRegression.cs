extern alias StorageModule;

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using BotAgent.Adapters.Panel;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Configuration;
using BotAgent.Services;
using BotAgent.Services.Conversations;

namespace BotAgent.ReviewRemediationProbe;

internal static class ReviewPanelRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        DeploymentPlan(check);
        ModelEnvironment(check);
        MaskedConversation(check);
        await RevokedStreamAsync(check);
        await RevokedQueuedStreamAsync(check);
        await SharedSseAdmissionGateAsync(check);
    }

    private static void DeploymentPlan(Action<bool, string> check)
    {
        var plan = typeof(WebUiServer).Assembly.GetType("BotAgent.Adapters.Panel.PanelDeploymentPlan")!;
        object Invoke(string name, params object[] args) => plan.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
        var previous = "sha256:" + new string('a', 64);
        var script = (string)Invoke("HelperScript", false, previous);
        check((string)Invoke("BackupArguments", false) == "tag qqchat-agent:latest qqchat-agent:rollback-current",
            "rollback captures current separately without overwriting prev");
        check(script.Contains("docker tag " + previous + " qqchat-agent:latest", StringComparison.Ordinal)
            && !script.Contains("docker tag qqchat-agent:prev", StringComparison.Ordinal), "rollback uses captured immutable selected image");
        check(script.Contains("up -d --no-deps qqchat", StringComparison.Ordinal) && script.Contains("exit $result", StringComparison.Ordinal),
            "helper affects bot only and preserves failing exit status");
        check((string)Invoke("BackupArguments", true) == "tag qqchat-agent:latest qqchat-agent:prev", "ordinary deploy still keeps prev");
        check(!(bool)Invoke("IsImageId", "sha256:abc; injected"), "rollback image id rejects shell input");
    }

    private static void ModelEnvironment(Action<bool, string> check)
    {
        string[] names = ["BOTAGENT_API_KEY", "BOTAGENT_API_KEY_FILE", "QQCHAT_API_KEY", "QQCHAT_API_KEY_FILE", "OPENAI_API_KEY", "OPENAI_API_KEY_FILE"];
        var original = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        var file = Path.Combine(Path.GetTempPath(), "synthetic-model-key-" + Guid.NewGuid().ToString("N"));
        var previous = new AppSettings { ApiKey = "synthetic-panel-key", ApiKeyOverride = "synthetic-panel-key" };
        var project = typeof(BotConfig).GetMethod("ProjectHotSaveSecrets", BindingFlags.Static | BindingFlags.NonPublic)!;
        var resolve = typeof(BotConfig).GetMethod("FixedModelEnvironmentKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        string? Resolve() => (string?)resolve.Invoke(null, null);
        AppSettings Delete()
        {
            var next = previous.Snapshot();
            project.Invoke(null, [next, previous, ImmutableArray.Create((WritableSecretSlot.ModelApiKey, SecretEditKind.Delete)),
                new Func<WritableSecretSlot, string?>(_ => null)]);
            return next;
        }
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("BOTAGENT_API_KEY", " synthetic-canonical-key ");
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", "synthetic-legacy-key");
            check(Delete().ApiKey == "synthetic-canonical-key" && Resolve() == "synthetic-canonical-key", "hot delete uses canonical before legacy");
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", null);
            check(Delete().ApiKey == "synthetic-canonical-key", "canonical-only hot delete remains configured");
            Environment.SetEnvironmentVariable("BOTAGENT_API_KEY", null);
            File.WriteAllText(file, " synthetic-file-key ");
            Environment.SetEnvironmentVariable("BOTAGENT_API_KEY_FILE", file);
            Environment.SetEnvironmentVariable("QQCHAT_API_KEY", "synthetic-legacy-key");
            check(Delete().ApiKey == "synthetic-file-key" && Resolve() == "synthetic-file-key", "canonical FILE before obsolete legacy direct value");
            var metadata = typeof(BotConfig).Assembly.GetType("BotAgent.Host.CompositionRoot")!.GetMethod("ConfigurationSecretMetadata", BindingFlags.NonPublic | BindingFlags.Static);
            if (metadata is not null)
            {
                using var connection = AppDatabase.Open();
                using var transaction = connection.BeginTransaction();
                var module = typeof(StorageModule::BotAgent.Storage.ConfigurationStorageBridge).Assembly;
                var mutation = module.GetType("BotAgent.Storage.SecretMutation")!;
                var value = Activator.CreateInstance(mutation, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [WritableSecretSlot.ModelApiKey, SecretEditKind.Delete], null)!;
                var empty = typeof(ImmutableArray<>).MakeGenericType(mutation).GetField("Empty")!.GetValue(null)!;
                var mutations = empty.GetType().GetMethod("Add")!.Invoke(empty, [value])!;
                var result = (ImmutableArray<SecretMetadata>)metadata.Invoke(null, [connection, Delete(), mutations])!;
                check(result.Any(m => m.Slot == WritableSecretSlot.ModelApiKey && m.Configured && m.Source == CredentialSource.Environment),
                    "secret metadata reports canonical FILE fallback without exposing value");
            }
        }
        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, original[name]);
            File.Delete(file);
        }
    }

    private static void MaskedConversation(Action<bool, string> check)
    {
        var box = new SettingsBox(new AppSettings { AgentMaskSensitive = true });
        var registry = new ConversationRegistry(null!, box, null!, _ => true, _ => { });
        var type = typeof(WebUiServer).Assembly.GetType("BotAgent.Adapters.Panel.PanelDto")!;
        var dto = Activator.CreateInstance(type, box, registry)!;
        System.Text.Json.Nodes.JsonObject Project(BotConversation item)
            => (System.Text.Json.Nodes.JsonObject)type.GetMethod("Conversation")!.Invoke(dto, [item])!;
        var conversation = new BotConversation { SourceKey = "group:10001", Name = "Synthetic Unique Group", Kind = BotAgent.Domain.Conversation.ConversationKind.GroupChat };
        var masked = Project(conversation);
        check(masked["name"]!.GetValue<string>() != conversation.Name, "unknown group title is masked independently of sender names");
        check(masked["nameRaw"]!.GetValue<string>() == conversation.Name && masked["key"]!.GetValue<string>() == conversation.SourceKey,
            "masked display preserves editable raw name and storage identity");
        box.Apply(s => s.AgentMaskSensitive = false);
        check(Project(conversation)["name"]!.GetValue<string>() == conversation.Name, "mask switch off retains ordinary display");
    }

    private static async Task SharedSseAdmissionGateAsync(Action<bool, string> check)
    {
        var type = typeof(WebUiServer).GetNestedType("SseClient", BindingFlags.NonPublic)!;
        var gate = new object();
        var stream = new HeldStream { AdmissionGate = gate };
        bool authorized = true, authWasLocked = false;
        var client = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null,
            [stream, new Func<bool>(() => { authWasLocked = Monitor.IsEntered(gate); return authorized; }), gate], null)!;
        var send = (Task)type.GetMethod("SendAsync")!.Invoke(client, ["synthetic", "contested-admission"])!;
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(authWasLocked && stream.WriteGateHeld == true, "SSE check and write admission share revocation gate");
        lock (gate) { authorized = false; ((IDisposable)client).Dispose(); }
        stream.Release.TrySetResult();
        try { await send.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { }
        check(stream.FlushCount == 0 && stream.ToArray().Length == 0, "revoked admitted SSE never initiates flush or commits blocked write");
        ((IDisposable)client).Dispose();
    }

    private static async Task RevokedQueuedStreamAsync(Action<bool, string> check)
    {
        var type = typeof(WebUiServer).GetNestedType("SseClient", BindingFlags.NonPublic)!;
        var authorizationGate = new object();
        var stream = new HeldStream();
        bool authorized = true;
        var client = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null,
            [stream, new Func<bool>(() => authorized), authorizationGate], null)!;
        Task Send(string value) => (Task)type.GetMethod("SendAsync")!.Invoke(client, ["synthetic", value])!;
        try
        {
            var first = Send("pending-before-revoke");
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued = Send("queued-before-revoke");
            lock (authorizationGate) { authorized = false; ((IDisposable)client).Dispose(); }
            stream.Release.TrySetResult();
            foreach (var task in new[] { first, queued })
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)); } catch (OperationCanceledException) { } catch (UnauthorizedAccessException) { }
            check(stream.ToArray().Length == 0, "revocation cancels in-flight and queued SSE frames before payload commit");
            var rejected = false;
            try { await Send("admitted-after-revoke"); } catch (OperationCanceledException) { rejected = true; }
            check(rejected && stream.ToArray().Length == 0, "post-revocation SSE admission is denied");
        }
        finally { ((IDisposable)client).Dispose(); }
    }

    private sealed class HeldStream : MemoryStream
    {
        internal object? AdmissionGate { get; init; }
        internal bool? WriteGateHeld { get; private set; }
        internal int FlushCount { get; private set; }
        public override Task FlushAsync(CancellationToken ct) { FlushCount++; return base.FlushAsync(ct); }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            WriteGateHeld = AdmissionGate is null ? null : Monitor.IsEntered(AdmissionGate);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            await base.WriteAsync(bytes, ct);
        }
        protected override void Dispose(bool disposing) { /* Preserve captured bytes for assertions only. */ }
    }

    private static async Task RevokedStreamAsync(Action<bool, string> check)
    {
        var type = typeof(WebUiServer).GetNestedType("SseClient", BindingFlags.NonPublic)!;
        var stream = new MemoryStream();
        bool authorized = true;
        var client = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null, [stream, new Func<bool>(() => authorized)], null)!;
        Task Send(string text) => (Task)type.GetMethod("SendAsync")!.Invoke(client, ["synthetic", text])!;
        try
        {
            await Send("before-revocation");
            var before = stream.ToArray();
            authorized = false;
            var denied = false;
            try { await Send("after-revocation"); } catch (UnauthorizedAccessException) { denied = true; }
            check(denied && stream.ToArray().SequenceEqual(before), "revoked SSE cannot write future frames");
            check(Encoding.UTF8.GetString(before).Contains("before-revocation", StringComparison.Ordinal), "authorized SSE framing still works");
        }
        finally { ((IDisposable)client).Dispose(); }
    }
}
