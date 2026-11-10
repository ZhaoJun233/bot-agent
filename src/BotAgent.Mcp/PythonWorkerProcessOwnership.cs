using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BotAgent.Mcp;

/// <summary>Owns ordinary descendants, not a security sandbox. Package code starts only after Attach succeeds.</summary>
internal abstract class PythonWorkerProcessOwnership : IDisposable
{
    public static PythonWorkerProcessOwnership Create()
    {
        if (OperatingSystem.IsWindows()) return new PythonWorkerProcessOwnershipWindows();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
            return new UnixProcessGroup();
        throw new PythonWorkerException("python_ownership_failed");
    }

    public abstract void Attach(Process process);
    public abstract void Terminate();
    public void Dispose() => Terminate();

    // The bootstrap calls setsid before acknowledging ownership. Ordinary children inherit
    // the group even after the leader exits. Deliberate setsid/detachment is not contained.
    private sealed class UnixProcessGroup : PythonWorkerProcessOwnership
    {
        private int _group;
        public override void Attach(Process process)
        {
            var id = process.Id;
            if (GetProcessGroup(id) != id) throw new PythonWorkerException("python_ownership_failed");
            if (Interlocked.CompareExchange(ref _group, id, 0) != 0)
                throw new PythonWorkerException("python_ownership_failed");
        }

        public override void Terminate()
        {
            var group = Interlocked.Exchange(ref _group, -1);
            if (group > 0) Kill(-group, 9);
        }

        [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
        private static extern int GetProcessGroup(int pid);
        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int Kill(int pid, int signal);
    }
}
