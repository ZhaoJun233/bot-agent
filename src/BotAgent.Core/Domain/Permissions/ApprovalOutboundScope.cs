namespace BotAgent.Domain.Permissions;

/// <summary>
/// 标记“当前发送的是服务端签发的系统审批公告”（而非模型生成的文本）。
/// 只有在此作用域内发出的合规格式公告，才允许平台层渲染带批准/拒绝按钮的交互卡片。
/// </summary>
public static class ApprovalOutboundScope
{
    private static readonly AsyncLocal<int> Depth = new();

    public static bool IsSystemAnnouncement => Depth.Value > 0;

    public static IDisposable EnterSystemAnnouncement()
    {
        var prior = Depth.Value;
        Depth.Value = prior + 1;
        return new Scope(prior);
    }

    private sealed class Scope(int prior) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Depth.Value = prior;
        }
    }
}
