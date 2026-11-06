using BotAgent.Domain.Ports;

internal sealed class ProbeClock : IClock
{
    public DateTimeOffset Now => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset UtcNow => Now;
    public DateTime LocalDateTime => Now.DateTime;
    public long TickCount => 1000;
    public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.Delay(delay, ct);
    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.Delay(millisecondsDelay, ct);
}
