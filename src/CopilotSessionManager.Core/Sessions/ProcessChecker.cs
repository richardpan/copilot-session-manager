using System.Diagnostics;

namespace CopilotSessionManager.Core.Sessions;

/// <inheritdoc />
public sealed class ProcessChecker : IProcessChecker
{
    public bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that PID.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has exited between lookup and HasExited probe.
            return false;
        }
    }
}
