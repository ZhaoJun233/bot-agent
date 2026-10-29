using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Qq;

/// <summary>
/// 可交换的通道门面：上层只拿它一次，内部实际指向哪个通道实现可以换。
///
/// 为什么需要它：通道层过去只在装配期构造一次，面板改了官方通道开关必须重启进程才生效 ——
/// 用户看到的「开了通道却一直未连接、日志里一行都没有」就是这么来的。
/// 门面让「保存配置 → 重建通道对象 → 热切换」成为可能，而不用让上层到处判断。
///
/// 换的时候有两条纪律：
///   ① 先退订旧实例的事件再切换引用 —— 否则一条消息会被新旧两个实例各转发一次；
///   ② 旧实例的 Stop / Dispose 由调用方负责 —— 这里只管解绑，不猜它内部还有什么要收尾。
/// </summary>
public sealed class SwappableChannelSource : IQqChatSource, IDisposable
{
    private readonly object _gate = new();
    private IQqChatSource _current;
    private bool _disposed;

    public SwappableChannelSource(IQqChatSource initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        Hook(initial);
    }

    public event Action<QqChatMessage>? MessageReceived;
    public event Action<QqPokeEvent>? Poked;
    public event Action<QqRecallEvent>? MessageRecalled;
    public event Action<bool>? ConnectionChanged;

    /// <summary>换掉内部实现，返回被换下来的旧实例（调用方负责 Stop / Dispose）。</summary>
    public IQqChatSource Swap(IQqChatSource next)
    {
        ArgumentNullException.ThrowIfNull(next);

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SwappableChannelSource));
            }

            var previous = _current;
            Unhook(previous);
            _current = next;
            Hook(next);
            return previous;
        }
    }

    public string Channel => _current.Channel;

    public bool IsConnected => _current.IsConnected;

    public void RegisterTarget(string channel, bool isGroup, long id)
        => _current.RegisterTarget(channel, isGroup, id);

    public Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => _current.SendMusicAsync(isGroup, targetId, platform, songId, title, ct);

    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        => _current.SendTextAsync(isGroup, targetId, text, ct, replyToMessageId, directAddress);

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => _current.GetMessageInfoAsync(messageId, ct);

    public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => _current.SendVoiceAsync(isGroup, targetId, audioUrl, ct);

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => _current.SendImageAsync(isGroup, targetId, data, ct, replyToMessageId);

    public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => _current.SendPokeAsync(isGroup, targetId, userId, ct);

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => _current.GetGroupNameAsync(groupId, ct);

    public Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
        => _current.GetGroupMemberInfoAsync(groupId, userId, ct);

    public Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
        => _current.FetchCustomFacesAsync(count, ct);

    public Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
        => _current.RefreshImageUrlsAsync(messageId, ct);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Unhook(_current);
        }
    }

    private void Hook(IQqChatSource src)
    {
        src.MessageReceived += OnInnerMessage;
        src.Poked += OnInnerPoked;
        src.MessageRecalled += OnInnerRecalled;
        src.ConnectionChanged += OnInnerConnection;
    }

    private void Unhook(IQqChatSource src)
    {
        src.MessageReceived -= OnInnerMessage;
        src.Poked -= OnInnerPoked;
        src.MessageRecalled -= OnInnerRecalled;
        src.ConnectionChanged -= OnInnerConnection;
    }

    private void OnInnerMessage(QqChatMessage msg) => MessageReceived?.Invoke(msg);

    private void OnInnerPoked(QqPokeEvent evt) => Poked?.Invoke(evt);

    private void OnInnerRecalled(QqRecallEvent evt) => MessageRecalled?.Invoke(evt);

    private void OnInnerConnection(bool connected) => ConnectionChanged?.Invoke(connected);
}