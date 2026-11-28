using System.Reflection;
using BotAgent.ReplyPipelinePacingProbe;

namespace BotAgent.ReplyModeTransitionProbe;

internal static class TransitionClock
{
    // Wait for actual timer registrations before advancing virtual time. Neither worker
    // counts nor thread-pool drains prove Delay has captured the current clock value.
    public static async Task AdvanceRegistered(Fixture f, int milliseconds)
    {
        var target = f.Clock.TickCount + milliseconds;
        while (f.Clock.TickCount < target)
        {
            await f.Until(() => NextDue(f.Clock) is not null || f.ActiveCount > 0, "paced timer registered");
            var due = NextDue(f.Clock) ?? target;
            await f.Advance((int)Math.Max(0, Math.Min(target, due) - f.Clock.TickCount));
        }
    }

    // Step through registered virtual timers while paced work is active, then finish
    // the requested window and wait for idle. Avoids both missed-timer races on bulk
    // Advance and hangs when paced work terminates early (e.g. probability rejection/stop).
    public static async Task AdvanceUntilIdle(Fixture f, int milliseconds)
    {
        var target = f.Clock.TickCount + milliseconds;
        while (f.Clock.TickCount < target && (f.PacedCount > 0 || f.ActiveCount > 0))
        {
            await f.Until(() => NextDue(f.Clock) is not null || f.ActiveCount > 0 || f.PacedCount == 0,
                "paced timer registered or idle");
            if (f.PacedCount == 0 && f.ActiveCount == 0) break;
            var due = NextDue(f.Clock) ?? target;
            var delta = Math.Min(target, due) - f.Clock.TickCount;
            if (delta <= 0) { await Fixture.Drain(); continue; }
            await f.Advance((int)delta);
        }
        if (f.Clock.TickCount < target)
            await f.Advance((int)(target - f.Clock.TickCount));
        await f.Idle();
    }

    private static long? NextDue(ManualClock clock)
    {
        var gate = typeof(ManualClock).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
        lock (gate)
        {
            var waits = (IEnumerable<(long Due, TaskCompletionSource Completion)>)typeof(ManualClock)
                .GetField("_waits", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(clock)!;
            return waits.Where(w => !w.Completion.Task.IsCompleted).Select(w => (long?)w.Due).Min();
        }
    }
}
