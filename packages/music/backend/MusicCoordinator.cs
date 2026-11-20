using BotAgent.Services.Music;

namespace BotAgent.Packages.Music;

/// <summary>
/// Package-owned music runtime coordinator: owns listening orchestration, panel self-test state,
/// share resolution + legacy link fallback, cooldown enforcement, and one-shot listening notes.
/// </summary>
public sealed class MusicCoordinator : IMusicRuntimeContribution
{
    private readonly MusicPolicyEngine _policy;
    private readonly IMusicHostPorts? _ports;
    private string? _lastTestHeader;

    public MusicCoordinator(MusicPolicyEngine? policy = null, IMusicHostPorts? ports = null)
    {
        _policy = policy ?? new MusicPolicyEngine();
        _ports = ports;
    }

    public string? LastTestHeader => Volatile.Read(ref _lastTestHeader);

    public bool TryBeginListen(string sourceKey, int cooldownSeconds, DateTimeOffset now, out string reason)
        => _policy.TryBeginListen(sourceKey, cooldownSeconds, now, out reason);

    public void AddNote(string sourceKey, string note)
        => _policy.AddNote(sourceKey, note);

    public bool HasNote(string sourceKey)
        => _policy.HasNote(sourceKey);

    public string? TakeNote(string sourceKey)
        => _policy.TakeNote(sourceKey);

    public bool TryRecordShare(string sourceKey, string songId, TimeSpan dedupWindow, DateTimeOffset now)
        => _policy.TryRecordShare(sourceKey, songId, dedupWindow, now);

    public void ClearConversation(string sourceKey)
        => _policy.ClearConversation(sourceKey);

    public async Task<string?> TestByNameAsync(string song, CancellationToken ct)
    {
        Volatile.Write(ref _lastTestHeader, null);
        if (_ports is null || !_ports.IsMediaReady)
        {
            return null;
        }

        var note = await _ports.DescribeByNameAsync(song, "面板自测", ct).ConfigureAwait(false);
        Volatile.Write(ref _lastTestHeader, song);
        _ports.Log(note is null
            ? $"[Music] 面板自测「{song}」：没搜到或没听到"
            : $"[Music] 面板自测「{song}」完成");
        return note;
    }

    public Task<string?> DescribeShareAsync(
        string platform,
        string? songId,
        string? title,
        string? artist,
        string? pageUrl,
        string? directAudioUrl,
        string sender,
        CancellationToken ct)
        => _ports is null || !_ports.IsMediaReady
            ? Task.FromResult<string?>(null)
            : _ports.DescribeShareAsync(platform, songId, title, artist, pageUrl, directAudioUrl, sender, ct);

    public async Task<string?> ListenAsync(string sourceKey, string song, string requester, CancellationToken ct)
    {
        if (_ports is null || !_ports.IsMediaReady)
        {
            return null;
        }

        try
        {
            var note = await _ports.DescribeByNameAsync(song, requester, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(note))
            {
                _ports.Log($"[Music] 没搜到/没听到「{song}」");
                return null;
            }

            _policy.AddNote(sourceKey, note!);
            return note;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _ports.Log($"[Music] 听「{song}」失败: {ex.Message}");
            return null;
        }
    }

    public async Task<MusicSendOutcome> ShareWithReceiptAsync(string song, bool isGroup, long targetId, CancellationToken ct)
    {
        if (_ports is null || !_ports.IsMediaReady || targetId == 0 || !_ports.IsSourceConnected)
        {
            return new(false, false, false, null, null, null);
        }

        var songId = await _ports.ResolveSongIdByNameAsync(song, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(songId))
        {
            _ports.Log("music_share outcome=unavailable");
            return new(false, false, false, null, null, null);
        }

        var sent = await _ports.SendMusicAsync(isGroup, targetId, songId, song, ct).ConfigureAwait(false);
        if (sent.HasDeliveryReceipt || sent.Ok)
        {
            var confirmed = sent.Ok && (!sent.HasDeliveryReceipt || sent.DeliverySucceeded);
            var status = sent.DeliveryStatus?.ToLowerInvariant() ?? (confirmed ? "success" : "failed");
            _ports.Log($"music_share outcome={status}");
            return sent with { Ok = confirmed };
        }

        // Only legacy boolean adapters retain their established link fallback.
        var link = $"https://music.163.com/song?id={songId}";
        var sentLink = await _ports.SendTextAsync(isGroup, targetId, link, ct).ConfigureAwait(false);
        var linkConfirmed = sentLink.Ok && (!sentLink.HasDeliveryReceipt || sentLink.DeliverySucceeded);
        _ports.Log($"music_share fallback=link confirmed={linkConfirmed}");
        return sentLink with { Ok = linkConfirmed };
    }
}
