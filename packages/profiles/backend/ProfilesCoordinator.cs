using BotAgent.Services.Profiles;

namespace BotAgent.Packages.Profiles;

/// <summary>
/// Package-owned profiles runtime coordinator: owns the background persona summarization timer,
/// per-item revocation checks, counters, and scoped profile presentation policy.
/// </summary>
public sealed class ProfilesCoordinator : IProfilesRuntimeContribution, IDisposable
{
    private readonly object _timerSync = new();
    private readonly SemaphoreSlim _summaryGate = new(1, 1);
    private readonly ProfilesPolicyEngine _policy;
    private readonly IProfilesHostPorts? _ports;
    private Timer? _summaryTimer;
    private Task? _activeSweep;
    private int _summaryRunning;
    private long _summaryDoneCount;
    private long _summaryFailCount;
    private int _disposed;

    public ProfilesCoordinator(ProfilesPolicyEngine? policy = null, IProfilesHostPorts? ports = null)
    {
        _policy = policy ?? new ProfilesPolicyEngine();
        _ports = ports;
    }

    public long SummaryDoneCount => Interlocked.Read(ref _summaryDoneCount);

    public long SummaryFailCount => Interlocked.Read(ref _summaryFailCount);

    public bool HasActiveSummaryTimer
    {
        get { lock (_timerSync) return _summaryTimer is not null; }
    }

    public bool AllowsProfileInjection(bool featureEnabled, bool hasAuthorizedScope)
        => _policy.AllowsProfileInjection(featureEnabled, hasAuthorizedScope);

    public bool AllowsMemoryMutation(bool featureEnabled, bool hasAuthorizedScope, bool legacySummaryDisabled)
        => _policy.AllowsMemoryMutation(featureEnabled, hasAuthorizedScope, legacySummaryDisabled);

    public string FormatProfileSummary(string displayName, string? manualOverride, string? autoSummary, int maxChars)
        => _policy.FormatProfileSummary(displayName, manualOverride, autoSummary, maxChars);

    public void NotifyConversationDeleted(string sourceKey)
        => _policy.NotifyConversationDeleted(sourceKey);

    public bool WasConversationDeleted(string sourceKey)
        => _policy.WasConversationDeleted(sourceKey);

    private bool SummarizationAllowed()
    {
        if (Volatile.Read(ref _disposed) != 0 || _ports is null)
        {
            return false;
        }

        var settings = _ports.ReadSummarySettings();
        return !settings.LegacySummaryDisabled
               && settings.EnableProfileSummary
               && !settings.HostDisposed
               && _ports.IsProfilesFeatureAllowed();
    }

    public void EnsureSummaryTimer(bool enabled, int intervalSeconds)
    {
        Timer? previous;
        lock (_timerSync)
        {
            previous = _summaryTimer;
            _summaryTimer = null;
            if (enabled && intervalSeconds > 0 && Volatile.Read(ref _disposed) == 0 && SummarizationAllowed())
            {
                _summaryTimer = new Timer(
                    _ => StartSweep(),
                    null,
                    TimeSpan.FromSeconds(Math.Min(5, intervalSeconds)),
                    TimeSpan.FromSeconds(intervalSeconds));
            }
        }
        previous?.Dispose();
    }

    public void StopSummaryTimer()
    {
        Timer? previous;
        lock (_timerSync)
        {
            previous = _summaryTimer;
            _summaryTimer = null;
        }
        previous?.Dispose();
    }

    private void StartSweep()
    {
        var task = RunSummarySweepAsync();
        Volatile.Write(ref _activeSweep, task);
        _ = task.ContinueWith(
            static (t, state) => Interlocked.CompareExchange(ref ((ProfilesCoordinator)state!)._activeSweep, null, t),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public async Task RunSummarySweepAsync()
    {
        if (!SummarizationAllowed() || _ports is null)
        {
            StopSummaryTimer();
            return;
        }

        if (Interlocked.Exchange(ref _summaryRunning, 1) == 1)
        {
            return;
        }

        try
        {
            var initial = _ports.ReadSummarySettings();
            var candidates = _ports.FindSummarizable(initial.Threshold, maxCandidates: 20);

            foreach (var c in candidates)
            {
                if (!SummarizationAllowed())
                {
                    break;
                }

                var acquired = false;
                try
                {
                    await _summaryGate.WaitAsync().ConfigureAwait(false);
                    acquired = true;
                    if (!SummarizationAllowed())
                    {
                        break;
                    }

                    var current = _ports.ReadSummarySettings();
                    var text = await _ports.SummarizeCandidateAsync(c, current.MaxChars).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        Interlocked.Increment(ref _summaryFailCount);
                        continue;
                    }

                    if (!SummarizationAllowed())
                    {
                        return;
                    }

                    if (!_ports.TryApplySummary(c.Uid, c.Scope, text, c.ThroughSeq, c.FoldedCount))
                        continue;
                    Interlocked.Increment(ref _summaryDoneCount);
                    _ports.Log($"画像已生成：{MaskLogToken(c.Name)}({MaskLogToken(c.Uid)}) @ {MaskLogToken(c.Scope)}（{c.NewMessages.Length} 条 → {text.Length} 字）");
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _summaryFailCount);
                    _ports.LogInternal($"画像摘要失败 {MaskLogToken(c.Uid)}@{MaskLogToken(c.Scope)}: {ex.Message}");
                }
                finally
                {
                    if (acquired)
                    {
                        try { _summaryGate.Release(); }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _ports.LogInternal("画像巡检异常: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _summaryRunning, 0);
        }
    }

    internal async Task ShutdownAsync(CancellationToken ct)
    {
        Dispose();
        if (Volatile.Read(ref _activeSweep) is { } active)
        {
            try { await active.WaitAsync(ct).ConfigureAwait(false); } catch { }
        }
        _summaryGate.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopSummaryTimer();
    }

    private static string MaskLogToken(string? value)
    {
        var s = value?.Trim() ?? string.Empty;
        if (s.Length == 0) return "*";
        if (s.Length <= 2) return s[0] + "*";
        if (s.Length <= 6) return $"{s[0]}***{s[^1]}";
        return $"{s[..3]}***{s[^2..]}";
    }
}
