using System.Net;
using System.Net.Sockets;

namespace BotAgent.Services.Net;

/// <summary>共享的出站地址分类，供安全 URL 校验和连接回调使用。</summary>
public static class OutboundAddressPolicy
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return IsBlocked(ip.MapToIPv4());
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || b[0] == 0
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            // fc00::/7 unique-local addresses are private even when the runtime
            // does not classify them as IsIPv6SiteLocal.
            return (b[0] & 0xfe) == 0xfc || b[0] == 0xff || b[0] == 0;
        }

        return false;
    }
}
