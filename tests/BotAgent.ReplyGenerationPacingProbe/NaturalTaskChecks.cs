using System.Reflection;
using System.Runtime.CompilerServices;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Reply;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Ops;
using BotAgent.Services.Reply;

internal static class NaturalTaskChecks
{
    public static Task DetachedCompletion() => Check(false);
    public static Task RetainedValidation() => Check(true);

    private static async Task Check(bool revokeSource)
    {
        // Invoke only the handoff seam; this incomplete fixture is never disposed or run as a pipeline.
        var pipeline = (ReplyPipeline)RuntimeHelpers.GetUninitializedObject(typeof(ReplyPipeline));
        var port = DispatchProxy.Create<IAgentCommandService, ModelPort>();
        Set("_hooks", new ReplyHooks(_ => { }, () => 10001, () => false));
        Set("_traces", new TurnTraceStore());
        Set("_agentCmds", port);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guard = new RetiringGuard();
        var owner = new object();
        var ambientOwner = new AsyncLocal<object?> { Value = owner };
        var revoked = false;
        var validations = 0;
        var dispatches = 0;
        var deliveries = 0;
        Task? child = null;
        ((ModelPort)(object)port).Call = (method, _) =>
        {
            if (method.Name != "RunNaturalSafeAsync") throw new InvalidOperationException("Unexpected task port call");
            dispatches++;
            return child = CompleteTask();
        };
        using var validation = ReplyCommitBoundary.EnterValidation(() =>
        {
            Interlocked.Increment(ref validations);
            if (revoked) throw new InvalidOperationException("synthetic_source_inactive");
        });
        using var scope = ReplyCommitBoundary.Enter(guard);
        var method = typeof(ReplyPipeline).GetMethod("TryDispatchNaturalAgentAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dispatched = await (Task<bool>)method.Invoke(pipeline, new object[]
        {
            new BotConversation { SourceKey = "group:10001" },
            new ChatMessage { Seq = 1, SenderId = 10001, Role = MessageRole.Peer, Text = "Synthetic task" },
            new CompletionResult { Action = ReplyAction.Agent, AgentTask = "Synthetic work", AgentAck = "Synthetic acknowledgement" },
            0d
        })!;
        Require(dispatched && dispatches == 1 && guard.Effects == 1, "Handoff starts exactly once after effect admission");
        guard.Retired = true;
        Require(ReplyCommitBoundary.IsActive, "Parent guard is restored after handoff");
        await ExpectFailure(ReplyCommitBoundary.BeforeSendAsync(), "retired_reply_generation");
        revoked = revokeSource;
        release.SetResult();
        if (revokeSource)
            await ExpectFailure(child!.WaitAsync(TimeSpan.FromSeconds(3)), "synthetic_source_inactive");
        else
        {
            await child!.WaitAsync(TimeSpan.FromSeconds(3));
            Require(deliveries == 2, "Independent acknowledgement and result complete after parent retirement");
        }
        if (revokeSource) Require(validations == 1, "Independent task retains source validation");
        Require(dispatches == 1, "Completion never dispatches the accepted task again");

        async Task CompleteTask()
        {
            await release.Task;
            Require(ReferenceEquals(ambientOwner.Value, owner), "Unrelated execution ownership survives handoff");
            // A task-owned boundary must inherit validation, not the retired reply guard.
            using var taskBoundary = revokeSource ? ReplyCommitBoundary.Enter(new RetiringGuard()) : null;
            await ReplyCommitBoundary.BeforeSendAsync();
            deliveries++;
            await ReplyCommitBoundary.BeforeSendAsync();
            deliveries++;
        }
        void Set(string field, object value) => typeof(ReplyPipeline)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(pipeline, value);
    }

    private static async Task ExpectFailure(Task task, string expected)
    {
        try { await task; }
        catch (InvalidOperationException ex) when (ex.Message == expected) { return; }
        throw new InvalidOperationException("Expected boundary failure: " + expected);
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
    private sealed class RetiringGuard : IReplyCommitGuard
    {
        public bool Retired;
        public int Effects;
        public Task BeforeEffectAsync() { Validate(); Effects++; return Task.CompletedTask; }
        public Task BeforeSendAsync() { Validate(); return Task.CompletedTask; }
        public Task BeforePrepareAsync() { Validate(); return Task.CompletedTask; }
        public bool TryBeforeSend() { Validate(); return true; }
        private void Validate()
        {
            if (Retired) throw new InvalidOperationException("retired_reply_generation");
        }
    }
}
