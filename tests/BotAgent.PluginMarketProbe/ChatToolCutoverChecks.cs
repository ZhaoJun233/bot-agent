extern alias StorageModule;

using System.Reflection;
using BotAgent.Adapters.Plugins;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Permissions;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Reply;
using BotAgent.Domain.Tools;
using BotAgent.Services;
using BotAgent.Services.Net;
using BotAgent.Services.Ops;
using BotAgent.Services.Participation;
using BotAgent.Services.Permissions;
using BotAgent.Services.Plugins;
using BotAgent.Services.Reply;
using BotAgent.Services.Resilience;
using BotAgent.Services.Tools;

internal partial class Program
{
    internal static async Task<int> RunChatToolCutoverChecks(string root)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        const string id = "preset.feature.websearch";
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = typeof(PluginPackageService).Assembly;
        var type = host.GetType("BotAgent.Host.PluginRuntime")!;
        var flags = new StorageModule::BotAgent.Storage.PluginStore();
        var previous = flags.LoadPluginStates().GetValueOrDefault(id, true);
        var model = new AdoptionModel(); var pages = new AdoptionPages();
        var services = (IServiceProvider)Activator.CreateInstance(host.GetType("BotAgent.Host.ResearchPluginServices")!,
            model, pages, new ToolCircuitBreaker("synthetic-chat"),
            (Func<ResearchOptions>)(() => new(true, true, "", 3, 30, 5, 2000, true, 5, 3)), Array.Empty<IDisposable>())!;
        try
        {
            flags.SetPluginEnabled(id, true);
            await using var runtime = (IAsyncDisposable)type.GetConstructors(hidden).Single()
                .Invoke([Path.Combine(root, "chat-tool-cutover"), (Action<string>)(_ => { })]);
            type.GetMethod("BindServices", hidden)!.Invoke(runtime, [services]);
            await (Task)type.GetMethod("StartAsync", hidden)!.Invoke(runtime, [CancellationToken.None])!;
            var registry = (PluginManager)type.GetProperty("Registry", hidden)!.GetValue(runtime)!;
            var packages = (PluginPackageService)type.GetProperty("Packages", hidden)!.GetValue(runtime)!;
            var settings = new AppSettings { EnableWebSearch = true, MaxAgentSteps = 2, WebSearchCooldownSeconds = 30,
                EnableMusic = false, EnableVoice = false, EnableStickers = false, EnablePoke = false };
            var box = new ChatToolBox(settings);
            var approvals = new ApprovalUseCase(box,
                new ApprovalHooks(_ => { }, (text, _) => text, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
                new SessionPolicyLedger(), new TurnTraceStore(), catalog: registry.GetToolCatalog);
            var caps = approvals.Capabilities;
            Check(ChatToolSpecs.Find("web.search") is null && ChatToolSpecs.Find("web.read") is null
                && !ChatCapabilitySet.FromSwitches(true, false, false, false, false, null).Check("web.search", "synthetic").Allow,
                "without adoption there are no fallback research declarations or permissions");
            Check(caps.Check("web.search", "synthetic").Allow && caps.Catalog.FindBinding("web.search")!.ProviderId == id,
                "permission snapshot uses the actual adopted declaration");
            Check(ReferenceEquals(caps, approvals.Capabilities) && model.Calls == 0 && pages.Calls == 0,
                "unchanged catalog keeps policy snapshot and discovery performs no IO");
            var directory = ToolDirectory.FromCatalog(caps.Catalog);
            Check(directory.IsHealthy && directory.Specs.Count == 26 && directory.Chat.Count == 10
                && directory.Executors.TryGet("research.actions", out var executor) && !executor.LegacyPath
                && executor.Implementation == id, "directory reports one adopted research executor without LegacyPath");
            var facade = new ResearchUseCase(registry);
            var gate = new PluginGate(registry);
            var participation = new ParticipationUseCase(box, _ => { });
            var visibility = new ReplyToolAvailability(gate, webReady: () => facade.IsReady, catalog: () => approvals.Capabilities.Catalog);
            var policy = new EffectivePlatformPolicy(new PlatformContext(PlatformId.QqPrivate, AccountScope.Legacy),
                true, true, true, true, PlatformCapabilities.QqOneBot, new HashSet<string>(), new Dictionary<string, bool>(), []);
            var inline = new InlineTurnTools(facade, approvals, participation, _ => { }, gate);
            var conversation = new BotConversation { SourceKey = "group:10001" };
            Check(visibility.Render(settings, policy, caps).Contains("web.search"), "prompt comes from adopted tool metadata");
            Check(await inline.RunAsync(conversation, settings, caps, new(null, null, null, Search: "x"), policy) is null
                && model.Calls == 0, "invalid inline arguments are rejected before IO and cooldown");
            Check((await inline.RunAsync(conversation, settings, caps, new(null, null, null, Search: "synthetic query"), policy))
                ?.Contains("synthetic answer") == true && model.Calls == 1, "inline chat uses package executor exactly once");
            Check(await inline.RunAsync(conversation, settings, caps, new(null, null, null, Search: "synthetic again"), policy) is null
                && model.Calls == 1, "same-generation coordinator preserves search cooldown");
            Check(facade.TakeNote(conversation.SourceKey) is not null && facade.TakeNote(conversation.SourceKey) is null,
                "tool result retains one-shot notes for reply flow");
            Check((await inline.RunAsync(conversation, settings, caps, new(null, null, null, Read: "https://example.com/page"), policy))
                ?.StartsWith("页面正文：") == true && pages.Calls == 1, "inline read uses package formatting and one page call");

            var jobs = new List<Func<CancellationToken, Task>>();
            var replies = 0;
            var actionsType = typeof(InlineTurnTools).Assembly.GetType("BotAgent.Services.Reply.ReplyTurnActions")!;
            var constructor = actionsType.GetConstructors().Single();
            var hooks = new ReplyHooks(_ => { }, () => 10001, () => false);
            var queue = (Func<string, string, Func<CancellationToken, Task>, bool>)((_, _, job) => { jobs.Add(job); return true; });
            var arguments = constructor.GetParameters().Select(parameter => parameter.Name switch
            {
                "box" => (object)box, "hooks" => hooks, "replyTools" => visibility, "research" => facade,
                "approvals" => approvals, "participation" => participation, "queueBackground" => queue,
                "requestReply" => (Action<BotConversation, AppSettings>)((_, _) => replies++),
                "allowsFeature" => (Func<string, bool>)(_ => true), _ => null
            }).ToArray();
            var actions = constructor.Invoke(arguments);
            async Task Enqueue(ChatCapabilitySet snapshot, string key, CompletionResult result)
                => await (Task)actionsType.GetMethod("QueueTurnActions")!.Invoke(actions,
                    [new BotConversation { SourceKey = key }, settings, snapshot, policy, result, false])!;
            await Enqueue(caps, "group:10002", new(null, null, null, Search: "synthetic queued"));
            await Enqueue(caps, "group:10002", new(null, null, null, Search: "synthetic duplicate"));
            Check(jobs.Count == 1 && model.Calls == 1, "queue-time cooldown admits one job before any IO");
            await jobs[0](default);
            Check(model.Calls == 2 && replies == 1, "queued chat executes package once and requests the next reply");
            await Enqueue(caps, "group:10003", new(null, null, null, Read: "file:///synthetic"));
            Check(jobs.Count == 1 && pages.Calls == 1, "queued argument validation rejects forbidden schemes");
            await Enqueue(caps, "group:10003", new(null, null, null, Read: "https://example.com/page"));
            Check(jobs.Count == 2, "read job captures its original generation");
            var old = caps.Catalog.FindBinding("web.search")!;
            registry.SetEnabled(id, false);
            Check(!visibility.Render(settings, policy, caps).Contains("web.search")
                && !approvals.Capabilities.Check("web.search", "synthetic").Allow,
                "disable removes old prompt tools and current permission without settings reload");
            registry.SetEnabled(id, true);
            await registry.RemoveAsync(id);
            await packages.ApplyAtStartupAsync(id, new PluginContext(new AdoptionContext(services)), registry, true);
            var next = approvals.Capabilities;
            Check(next.Policy.PolicyVersion > caps.Policy.PolicyVersion && next.PolicyFingerprint != caps.PolicyFingerprint
                && next.Catalog.FindBinding("web.search")!.Generation != old.Generation,
                "same version replacement changes metadata-policy identity");
            Check(await inline.RunAsync(conversation, settings, caps, new(null, null, null, Search: "synthetic stale"), policy) is null
                && model.Calls == 2, "old turn never resolves the new executor");
            await jobs[1](default);
            Check(pages.Calls == 1 && replies == 1, "old queued read neither calls new generation nor wakes a reply");
            try { old.GetContribution<IResearchUseCase>().TryBeginSearch("group:10004", 30, out _); }
            catch (InvalidOperationException error) when (error.Message is "plugin_dependency_unavailable" or "plugin_contribution_unavailable")
            { checks++; goto RejectedAuxiliary; }
            throw new InvalidOperationException("old auxiliary contribution resolved a newer generation");
        RejectedAuxiliary:
            Check((await inline.RunAsync(new BotConversation { SourceKey = "group:10004" }, settings, next,
                new(null, null, null, Search: "synthetic fresh"), policy)) is not null && model.Calls == 3,
                "new generation has fresh cooldown and one executor");
            foreach (var definition in new[] { Definition("chat.reply"), Definition("synthetic.other") with
                { Spec = Definition("synthetic.other").Spec with { Executor = ChatToolSpecs.ExecutorActions } } })
            {
                try { await registry.ReplaceAsync(new ToolOwnerPlugin("synthetic.collision", definition), true); }
                catch (InvalidOperationException error) when (error.Message == "plugin_tool_conflict") { checks++; continue; }
                throw new InvalidOperationException("legacy tool or executor collision was published");
            }
            Check(registry.IsReady(id) && registry.GetToolCatalog().Bindings.Count == 2,
                "failed legacy collision preserves adopted research");
        }
        finally { flags.SetPluginEnabled(id, previous); }
        return checks;
    }

    private sealed class ChatToolBox(AppSettings settings) : ISettingsBox { public AppSettings Current => settings; }
}
