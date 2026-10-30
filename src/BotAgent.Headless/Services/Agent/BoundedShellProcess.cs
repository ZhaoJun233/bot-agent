using System.Diagnostics;
using System.Text;

namespace BotAgent.Services.Agent;

/// <summary>Drain both pipes while the command runs, retaining only fixed-size prefixes.</summary>
internal static class BoundedShellProcess
{
    internal const int CaptureCharactersPerStream = 4000;
    private const int ReadBufferCharacters = 1024;

    internal sealed record Result(int? ExitCode, string Stdout, string Stderr, bool Truncated, string? Failure);

    public static async Task<Result> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(null, "", "", false, "process_start_failed");
        }
        catch (Exception ex)
        {
            return new(null, "", "", false, "process_start_failed:" + ex.GetType().Name);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var stdout = new Capture();
        var stderr = new Capture();
        var stdoutTask = DrainAsync(process.StandardOutput, stdout, deadline);
        var stderrTask = DrainAsync(process.StandardError, stderr, deadline);
        var exitTask = process.WaitForExitAsync(deadline.Token);
        var completed = false;
        string? failure = null;
        try
        {
            // The deadline includes EOF: descendants can keep pipes open after the shell exits.
            await Task.WhenAll(exitTask, stdoutTask, stderrTask);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            failure = stdout.Error ?? stderr.Error ?? "command_timeout";
        }
        catch (Exception ex)
        {
            failure = stdout.Error ?? stderr.Error ?? "process_wait_failed:" + ex.GetType().Name;
        }
        finally
        {
            if (!completed)
            {
                TryKillTree(process);
                deadline.Cancel();
                process.StandardOutput.Dispose();
                process.StandardError.Dispose();
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(cleanupTimeout.Token); }
                catch { failure = "process_cleanup_incomplete:" + failure; }
            }
            // Always observe both readers and the exit waiter, including cancellation/failure exits.
            try { await Task.WhenAll(exitTask, stdoutTask, stderrTask); }
            catch { /* The failure is reported below; caller cancellation is rethrown. */ }
        }

        ct.ThrowIfCancellationRequested();
        return new(completed ? process.ExitCode : null, stdout.Text.ToString(), stderr.Text.ToString(),
            stdout.Truncated || stderr.Truncated, failure);
    }

    private sealed class Capture
    {
        public StringBuilder Text { get; } = new(CaptureCharactersPerStream);
        public bool Truncated { get; set; }
        public string? Error { get; set; }
    }

    private static async Task DrainAsync(StreamReader reader, Capture capture, CancellationTokenSource deadline)
    {
        var buffer = new char[ReadBufferCharacters];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), deadline.Token);
                if (read == 0) return;
                var keep = Math.Min(read, CaptureCharactersPerStream - capture.Text.Length);
                capture.Text.Append(buffer, 0, keep);
                if (keep < read) capture.Truncated = true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            capture.Error = "output_read_failed:" + ex.GetType().Name;
            deadline.Cancel();
            throw;
        }
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Already exited. */ }
        catch (System.ComponentModel.Win32Exception) { /* OS cleanup is best effort. */ }
    }
}
