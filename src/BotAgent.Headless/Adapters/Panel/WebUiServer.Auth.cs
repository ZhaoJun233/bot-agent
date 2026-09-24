using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private Task HandleAuthStatusAsync(HttpListenerContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        return WriteJsonAsync(context, 200, new JsonObject
        {
            ["authenticated"] = IsAuthorized(context),
            ["mustChangePassword"] = HasPendingSession(context),
            ["legacyTokenConfigured"] = !string.IsNullOrWhiteSpace(_settings.PanelToken)
        });
    }

    private async Task HandleAuthLoginAsync(HttpListenerContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var body = await ReadAuthBodyAsync(context);
        var password = body?["password"]?.GetValue<string>();
        if (password is null || password.Length > 200)
        {
            await AuthErrorAsync(context, 400, "请输入面板密码");
            return;
        }

        bool valid;
        lock (_loginGate)
        {
            if (Clock.Now < _loginBlockedUntil)
            {
                valid = false;
            }
            else
            {
                valid = _panelPassword.Verify(password);
                _loginFailures = valid ? 0 : _loginFailures + 1;
                if (_loginFailures >= 5)
                {
                    _loginBlockedUntil = Clock.Now.AddSeconds(30);
                    _loginFailures = 0;
                }
            }
        }
        if (Clock.Now < _loginBlockedUntil)
        {
            await AuthErrorAsync(context, 429, "尝试过于频繁，请 30 秒后重试");
            return;
        }
        if (!valid)
        {
            await AuthErrorAsync(context, 401, "密码不正确");
            return;
        }
        SetPanelSession(context);
        await WriteJsonAsync(context, 200, new JsonObject { ["mustChangePassword"] = _panelPassword.MustChange });
    }

    private async Task HandleAuthChangeAsync(HttpListenerContext context)
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        var body = await ReadAuthBodyAsync(context);
        var current = body?["currentPassword"]?.GetValue<string>();
        var next = body?["newPassword"]?.GetValue<string>();
        if (current is null || next is null || next.Length < 10 || next.Length > 200 || string.IsNullOrWhiteSpace(next))
        {
            await AuthErrorAsync(context, 400, "新密码至少 10 位，最多 200 位");
            return;
        }
        lock (_loginGate)
        {
            if (!_panelPassword.Verify(current) || _panelPassword.Verify(next))
            {
                next = null;
            }
            else
            {
                _panelPassword.Change(next);
                _panelSessions.Clear();
            }
        }
        if (next is null)
        {
            await AuthErrorAsync(context, 400, "当前密码错误，或新密码与旧密码相同");
            return;
        }
        SetPanelSession(context);
        FileLog.Write("Web", "面板密码已修改");
        await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true });
    }

    private void SetPanelSession(HttpListenerContext context)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _panelSessions[token] = Clock.Now.AddHours(12);
        var secure = context.Request.IsSecureConnection ||
            string.Equals(context.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
        context.Response.Headers.Add("Set-Cookie", $"panel_session={token}; Path=/; Max-Age=43200; HttpOnly; SameSite=Strict{(secure ? "; Secure" : "")}");
    }

    private static async Task<JsonNode?> ReadAuthBodyAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > 4096) return null;
        var buffer = new byte[4097];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await context.Request.InputStream.ReadAsync(buffer.AsMemory(count));
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count > 4096) return null;
        try { return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(buffer, 0, count)); }
        catch { return null; }
    }

    private static Task AuthErrorAsync(HttpListenerContext context, int status, string message) =>
        WriteJsonAsync(context, status, new JsonObject { ["error"] = message });
}
