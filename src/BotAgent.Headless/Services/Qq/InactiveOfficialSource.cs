using BotAgent.Domain.Qq;
using BotAgent.Services.OneBot;

namespace BotAgent.Services.Qq;

/// <summary>
/// 官方通道的「未启用」占位实现。
///
/// 为什么不让装配期直接传 null：官方通道现在挂在一个可交换门面上（见 SwappableChannelSource），
/// 门面必须始终指向一个可用实例。关闭官方通道时就用这个空壳 —— 所有发送返回失败、
/// 连接状态恒为 false，行为与「这条通道不存在」一致，但门面本身不需要知道 null 这种状态。
/// </summary>
public sealed class InactiveOfficialSource : IQqChatSource
{
    public event Action<QqChatMessage>? MessageReceived
    {
        add { }
        remove { }
    }

    public event Action<QqPokeEvent>? Poked
    {
        add { }
        remove { }
    }

    public event Action<QqRecallEvent>? MessageRecalled
    {
        add { }
        remove { }
    }

    public event Action<bool>? ConnectionChanged
    {
        add { }
        remove { }
    }

    public string Channel => Channels.Official;

    public bool IsConnected => false;

    public void RegisterTarget(string channel, bool isGroup, long id)
    {
    }

    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
        => Task.FromResult(new SendResult(false));

    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));

    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Task.FromResult(false);

    public Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
        => Task.FromResult<GroupMemberInfo?>(null);

    public Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
        => Task.FromResult(new List<string>());

    public Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}