using System.Diagnostics;
using System.Text;

namespace BotAgent.Mcp;

public sealed record PythonWorkerInterpreterStatus(bool Available, bool Compatible, string? Version, string ReasonCode);

/// <summary>Runs only the operator-selected interpreter's --version; never imports package code.</summary>
public static class PythonWorkerInterpreter
{
    public static async Task<PythonWorkerInterpreterStatus> CheckAsync(
        string executablePath, string requiredVersion, CancellationToken ct)
    {
        if (!System.Version.TryParse(requiredVersion, out var minimum) || minimum.Major != 3
            || minimum.Minor is < 11 or > 99 || requiredVersion != $"3.{minimum.Minor}")
            return new(false, false, null, "python_invalid_required_version");
        if (string.IsNullOrWhiteSpace(executablePath)) return new(false, false, null, "python_interpreter_unavailable");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        } };
        process.StartInfo.ArgumentList.Add("--version");
        process.StartInfo.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) process.StartInfo.Environment[key] = value;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!process.Start()) return new(false, false, null, "python_interpreter_unavailable");
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            var text = (string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result : stdout.Result).Trim();
            if (process.ExitCode != 0 || !text.StartsWith("Python ", StringComparison.Ordinal)
                || !System.Version.TryParse(text[7..], out var actual))
                return new(false, false, null, "python_interpreter_invalid");
            var compatible = actual.Major == 3 && actual >= minimum;
            return new(true, compatible, actual.ToString(), compatible ? "python_ready" : "python_version_incompatible");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, false, null, "python_interpreter_timeout"); }
        catch { return new(false, false, null, "python_interpreter_unavailable"); }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[256];
        var used = 0;
        while (used < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(used), ct).ConfigureAwait(false);
            if (count == 0) return Encoding.UTF8.GetString(bytes, 0, used);
            used += count;
        }
        throw new IOException("Interpreter version output exceeded bound.");
    }
}
