using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BotAgent.Services;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private const int MaxPanelSessions = 256;
    private const int ClientLoginFailureLimit = 5;
    private const int GlobalLoginFailureLimit = 50;
    private static readonly TimeSpan PanelSessionLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan LoginFailureWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginBlockDuration = TimeSpan.FromSeconds(30);

    private sealed class LoginFailureState
    {
        public int Failures;
        public DateTimeOffset WindowStarted;
        public DateTimeOffset BlockedUntil;
    }

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

        var clientKey = GetClientKey(context);
        var valid = false;
        var rateLimited = false;
        lock (_loginGate)
        {
            var now = Clock.Now;
            ResetGlobalFailureWindow(now);
            if (now < _globalLoginBlockedUntil || IsClientBlocked(clientKey, now))
            {
                rateLimited = true;
            }
            else
            {
                valid = _panelPassword.Verify(password);
                if (valid)
                {
                    _loginFailuresByClient.Remove(clientKey);
                }
                else
                {
                    RecordFailedLogin(clientKey, now);
                    rateLimited = now < _globalLoginBlockedUntil || IsClientBlocked(clientKey, now);
                }
            }
        }

        if (rateLimited)
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
        lock (_loginGate)
        {
            var now = Clock.Now;
            CleanupExpiredPanelSessions(now);
            EvictPanelSessionsAtCapacity();

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _panelSessions[token] = now.Add(PanelSessionLifetime);
            var secure = context.Request.IsSecureConnection ||
                string.Equals(context.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
            context.Response.Headers.Add("Set-Cookie", $"panel_session={token}; Path=/; Max-Age=43200; HttpOnly; SameSite=Strict{(secure ? "; Secure" : "")}");
        }
    }

    private bool IsClientBlocked(string clientKey, DateTimeOffset now)
    {
        if (!_loginFailuresByClient.TryGetValue(clientKey, out var state)) return false;
        if (state.BlockedUntil > now) return true;
        if (state.WindowStarted == default || now < state.WindowStarted || now - state.WindowStarted >= LoginFailureWindow)
        {
            _loginFailuresByClient.Remove(clientKey);
        }
        return false;
    }

    private void RecordFailedLogin(string clientKey, DateTimeOffset now)
    {
        if (!_loginFailuresByClient.TryGetValue(clientKey, out var state) ||
            state.WindowStarted == default || now < state.WindowStarted || now - state.WindowStarted >= LoginFailureWindow)
        {
            state = new LoginFailureState { WindowStarted = now };
            _loginFailuresByClient[clientKey] = state;
        }

        state.Failures++;
        if (state.Failures >= ClientLoginFailureLimit)
        {
            state.Failures = 0;
            state.BlockedUntil = now.Add(LoginBlockDuration);
        }

        _globalLoginFailures++;
        if (_globalLoginFailures >= GlobalLoginFailureLimit)
        {
            _globalLoginFailures = 0;
            _globalLoginBlockedUntil = now.Add(LoginBlockDuration);
        }
    }

    private void ResetGlobalFailureWindow(DateTimeOffset now)
    {
        CleanupStaleLoginFailureStates(now);
        if (_globalLoginFailureWindowStarted == default || now < _globalLoginFailureWindowStarted ||
            now - _globalLoginFailureWindowStarted >= LoginFailureWindow)
        {
            _globalLoginFailureWindowStarted = now;
            _globalLoginFailures = 0;
        }
    }

    private void CleanupStaleLoginFailureStates(DateTimeOffset now)
    {
        foreach (var pair in _loginFailuresByClient)
        {
            var state = pair.Value;
            if (state.BlockedUntil <= now &&
                (state.WindowStarted == default || now < state.WindowStarted || now - state.WindowStarted >= LoginFailureWindow))
            {
                _loginFailuresByClient.Remove(pair.Key);
            }
        }
    }

    private static string GetClientKey(HttpListenerContext context)
    {
        var remote = context.Request.RemoteEndPoint?.Address;
        if (remote is not null && !IPAddress.IsLoopback(remote))
            return "remote:" + remote;

        foreach (var headerName in new[] { "X-Forwarded-For", "X-Real-IP" })
        {
            var forwarded = context.Request.Headers[headerName];
            var first = forwarded?.Split(',', 2)[0].Trim();
            if (!string.IsNullOrEmpty(first) && first.Length <= 128)
                return "forwarded:" + first;
        }

        return remote is null ? "remote:unknown" : "remote:" + remote;
    }

    private void CleanupExpiredPanelSessions(DateTimeOffset now)
    {
        foreach (var pair in _panelSessions)
        {
            if (pair.Value <= now)
                _panelSessions.TryRemove(pair.Key, out _);
        }
    }

    private void EvictPanelSessionsAtCapacity()
    {
        while (_panelSessions.Count >= MaxPanelSessions)
        {
            string? oldestToken = null;
            var oldestExpiry = DateTimeOffset.MaxValue;
            foreach (var pair in _panelSessions)
            {
                if (pair.Value < oldestExpiry)
                {
                    oldestToken = pair.Key;
                    oldestExpiry = pair.Value;
                }
            }

            if (oldestToken is null || !_panelSessions.TryRemove(oldestToken, out _))
                break;
        }
    }

    private bool TryGetValidPanelSession(HttpListenerContext context, DateTimeOffset now)
    {
        CleanupExpiredPanelSessions(now);
        var session = context.Request.Cookies["panel_session"]?.Value;
        return session is not null && _panelSessions.TryGetValue(session, out var expires) && expires > now;
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
