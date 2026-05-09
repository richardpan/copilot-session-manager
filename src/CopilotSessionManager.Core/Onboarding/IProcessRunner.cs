namespace CopilotSessionManager.Core.Onboarding;

/// <summary>
/// Result of running a process to completion. Captures stdout/stderr and the
/// exit code so the prerequisite checker can probe CLIs without managing
/// <see cref="System.Diagnostics.Process"/> directly.
/// </summary>
public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>True when the process exited with code 0.</summary>
    public bool Success => ExitCode == 0;

    /// <summary>Sentinel result used when the executable could not be located on PATH.</summary>
    public static ProcessRunResult NotFound { get; } = new(ExitCode: -1, StdOut: "", StdErr: "executable not found");
}

/// <summary>
/// Description of a process to run synchronously and capture output from.
/// </summary>
/// <param name="FileName">Executable name (resolved via PATH) or absolute path.</param>
/// <param name="Arguments">Argument tokens; passed to <c>ProcessStartInfo.ArgumentList</c>.</param>
/// <param name="TimeoutSeconds">Hard kill if the process hasn't exited within this window. Default 10s.</param>
public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds = 10);

/// <summary>
/// Runs an external process to completion and returns its captured output.
/// Distinct from <see cref="IProcessLauncher"/> (Sessions namespace) which is
/// fire-and-forget for spawning interactive PowerShell windows. This shim is
/// used by the prerequisite checker to probe CLI versions.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="request"/> and returns its stdout/stderr/exit code.
    /// Returns <see cref="ProcessRunResult.NotFound"/> when the executable
    /// cannot be located. Never throws for ordinary CLI failures.
    /// </summary>
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default);
}
