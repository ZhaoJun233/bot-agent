using BotAgent.Engine.Runtime;
namespace BotAgent.Services.Reply;

/// <summary>Gate generations live until all reserved holders and waiters drain.</summary>
internal sealed class ResizableReplyGate
{
    private readonly object _sync = new();
    private Generation _current;

    public ResizableReplyGate(int permits) => _current = new Generation(Validate(permits));

    public Lease Reserve()
    {
        lock (_sync)
        {
            _current.References++;
            return new Lease(this, _current);
        }
    }

    public void Resize(int permits)
    {
        permits = Validate(permits);
        lock (_sync)
        {
            if (_current.Permits == permits) return;
            var old = _current;
            _current = new Generation(permits);
            old.Retired = true;
            if (old.References == 0) old.Semaphore.Dispose();
        }
    }

    private static int Validate(int permits)
        => permits is >= 1 and <= 16 ? permits : throw new ArgumentOutOfRangeException(nameof(permits));

    private void Release(Generation generation, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) generation.Semaphore.Release();
            generation.References--;
            if (generation.Retired && generation.References == 0) generation.Semaphore.Dispose();
        }
    }

    internal sealed class Generation(int permits)
    {
        public readonly int Permits = permits;
        public readonly SemaphoreSlim Semaphore = new(permits, permits);
        public int References;
        public bool Retired;
    }

    internal sealed class Lease(ResizableReplyGate owner, Generation generation) : IDisposable
    {
        private bool _acquired;
        private int _disposed;

        public async Task WaitAsync(CancellationToken ct = default)
        {
            await generation.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            _acquired = true;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(generation, _acquired);
        }
    }
}
