using System.Net;

namespace BotAgent.Panel;

/// <summary>把 in-box HttpListener 的一对请求/响应桥到 <see cref="ZbaEndpoint" />。不在这里做鉴权或路由。</summary>
public static class ZbaHttpListenerAdapter
{
    public static async Task HandleAsync(
        HttpListenerContext context,
        ZbaEndpoint endpoint,
        int maxBodyBytes,
        CancellationToken ct)
    {
        var request = context.Request;
        var body = await ReadBodyAsync(request, maxBodyBytes, ct).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in request.Headers)
        {
            var values = request.Headers.GetValues(name);
            if (values is { Length: > 0 }) headers[name] = string.Join(",", values);
        }

        var zbaRequest = new ZbaRequest(
            request.HttpMethod,
            request.Url?.AbsolutePath ?? "/",
            request.Url?.Query ?? string.Empty,
            headers,
            body,
            request.RemoteEndPoint?.Address.ToString());

        var response = await endpoint.ProcessAsync(zbaRequest, ct).ConfigureAwait(false);
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        foreach (var (name, value) in response.Headers)
        {
            try
            {
                context.Response.Headers[name] = value;
            }
            catch (ArgumentException)
            {
                // HttpListener 拒绝受保护的响应头时忽略；正文已经带安全错误码。
            }
        }

        if (body.Length > maxBodyBytes) context.Response.KeepAlive = false;
        context.Response.ContentLength64 = response.Body.Length;
        if (response.Body.Length > 0)
            await context.Response.OutputStream.WriteAsync(response.Body, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBodyAsync(
        HttpListenerRequest request, int maxBodyBytes, CancellationToken ct)
    {
        var limit = maxBodyBytes + 1;
        using var buffer = new MemoryStream(Math.Min(limit, 16 * 1024));
        var chunk = new byte[8192];
        try
        {
            while (buffer.Length < limit)
            {
                var remaining = limit - (int)buffer.Length;
                var read = await request.InputStream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), ct)
                    .ConfigureAwait(false);
                if (read == 0) break;
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            Array.Clear(chunk);
        }

        return buffer.ToArray();
    }
}
