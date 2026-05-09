using System.Diagnostics;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// Description of a process to start. Mirrors the subset of
/// <see cref="ProcessStartInfo"/> we actually need so tests can assert on
/// what would have been spawned without ever calling <see cref="Process.Start(ProcessStartInfo)"/>.
/// </summary>
public sealed record ProcessStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    bool UseShellExecute);

/// <summary>
/// Thin abstraction over <see cref="Process.Start(ProcessStartInfo)"/> so the
/// rest of the codebase can be tested without spawning real processes.
/// </summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Starts the requested process. Returns the started PID, or null if the
    /// underlying API returned no process handle.
    /// </summary>
    int? Start(ProcessStartRequest request);
}

/// <summary>
/// Production <see cref="IProcessLauncher"/> backed by <see cref="Process.Start(ProcessStartInfo)"/>.
/// </summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public int? Start(ProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = request.UseShellExecute,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi);
        return process?.Id;
    }
}
