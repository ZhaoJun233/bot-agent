using System.Net;
using System.Net.Sockets;

namespace BotAgent.IntegrationHarness;

/// <summary>
/// 给各假服务用的 HttpListener 绑定器：先试场景约定的端口，被占就换一个系统空闲端口。
///
/// 为什么需要：约定端口会被**别的进程的一条普通出站连接**占成临时端口，
/// 这时 HttpListener.Start() 抛 <see cref="HttpListenerException"/>（32「另一个程序正在使用此文件」）。
/// 在 CI 上这表现为整轮 harness 直接崩掉（S26 实测：通过 360、失败 1、后续场景全没跑），
/// 而根因与待测代码毫无关系。端口只是测试内部的协调细节，换一个不影响任何断言。
///
/// 注意：Start() 失败时 HttpListener 内部会 Close()，实例随即处于已释放状态，
/// 所以换端口必须**换一个新实例**（这也是 <paramref name="listener"/> 用 ref 传的原因）。
/// </summary>
internal static class SyntheticListener
{
    /// <summary>
    /// 启动监听，返回真正绑上的端口；<paramref name="preferredPort"/> 可用时就是它本身。
    /// 换端口时会把 <paramref name="listener"/> 替换成新实例。
    /// </summary>
    public static int Start(ref HttpListener listener, string bind, int preferredPort)
    {
        listener.Prefixes.Add($"http://{bind}:{preferredPort}/");
        try
        {
            listener.Start();
            return preferredPort;
        }
        catch (HttpListenerException)
        {
            // 失败后实例已不可用（内部已 Close），直接丢掉换新的。
            listener = new HttpListener();
        }

        var port = FreePort();
        listener.Prefixes.Add($"http://{bind}:{port}/");
        listener.Start();
        return port;
    }

    /// <summary>让系统分配一个当前空闲的端口（临时端口层面的 TOCTOU 只能靠重试兜住）。</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
