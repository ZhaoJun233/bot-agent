using BotAgent.Domain.Ports;
using BotAgent.Model.Net;

internal sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IModelHttpFetcher
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
    public int Calls { get; private set; }
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        return await send(request, ct);
    }
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken ct = default) => SendAsync(request, ct);
    public Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(Uri url, HttpCompletionOption completionOption, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> GetAsync(string url, HttpCompletionOption completionOption, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<HttpResponseMessage> PostAsync(string url, HttpContent content, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.UnixEpoch;
    public DateTimeOffset UtcNow => Now;
    public DateTime LocalDateTime => Now.DateTime;
    public long TickCount => 0;
    public int Delays { get; private set; }
    public Task Delay(TimeSpan delay, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Delays++;
        return Task.CompletedTask;
    }
    public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Delay(TimeSpan.FromMilliseconds(millisecondsDelay), ct);
}
