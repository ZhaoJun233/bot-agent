using System.Net;
using System.Text.Json;

namespace BotAgent.Host;

/// <summary>
/// 只读 CLI：只发 GET，只打印服务端返回的安全投影；不启动宿主、不读写数据库、不打印凭据。
/// 令牌只从环境变量读取（BOTAGENT_ZBA_READ_TOKEN 优先，回退 BOTAGENT_ZBA_TOKEN）。
/// </summary>
internal static class ZbaCli
{
    private static readonly string[] Commands = { "--status", "--capabilities", "--tasks", "--events", "--zba-meta" };

    internal static bool IsRequest(string[] args) => Commands.Any(args.Contains);

    internal static async Task<int> RunAsync(string[] args, int port, HttpClient http)
    {
        var token = Clean(Environment.GetEnvironmentVariable("BOTAGENT_ZBA_READ_TOKEN"))
                    ?? Clean(Environment.GetEnvironmentVariable("QQCHAT_ZBA_READ_TOKEN"))
                    ?? Clean(Environment.GetEnvironmentVariable("BOTAGENT_ZBA_TOKEN"))
                    ?? Clean(Environment.GetEnvironmentVariable("QQCHAT_ZBA_TOKEN"));
        var baseUrl = Clean(Environment.GetEnvironmentVariable("BOTAGENT_ZBA_URL"))
                      ?? Clean(Environment.GetEnvironmentVariable("QQCHAT_ZBA_URL"))
                      ?? $"http://127.0.0.1:{port}/zba/v1";

        var path = args.Contains("--status") ? "status"
            : args.Contains("--capabilities") ? "capabilities?limit=200"
            : args.Contains("--tasks") ? "tasks?limit=50"
            : args.Contains("--events") ? "events?after=" + After(args) + "&limit=100"
            : "meta";

        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/" + path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        try
        {
            using var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"zba_{response.StatusCode.ToString().ToLowerInvariant()}: {SafeCode(body)}");
                return 1;
            }

            using var document = JsonDocument.Parse(body);
            Console.WriteLine(JsonSerializer.Serialize(document.RootElement,
                new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("zba_unavailable: " + exception.GetType().Name);
            return 1;
        }
    }

    private static int After(string[] args)
    {
        var index = Array.IndexOf(args, "--after");
        return index >= 0 && index + 1 < args.Length
               && int.TryParse(args[index + 1], out var value) && value >= 0 ? value : 0;
    }

    private static string SafeCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.String)
                return code.GetString() ?? "error";
        }
        catch (JsonException)
        {
            // 非 JSON 错误体不转发原文
        }

        return "error";
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
