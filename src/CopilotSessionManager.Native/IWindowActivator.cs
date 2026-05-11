namespace CopilotSessionManager.Native;

/// <summary>
/// Outcome of an attempt to bring a previously-launched session window to
/// the foreground (#104).
/// </summary>
public enum WindowActivationResult
{
    /// <summary>The window was found and pushed to the foreground.</summary>
    Activated,

    /// <summary>The PID is no longer running. Caller should re-launch.</summary>
    ProcessNotRunning,

    /// <summary>
    /// The process is alive but does not yet have a top-level window with
    /// a visible handle. Caller may retry or launch a fresh window.
    /// </summary>
    NoMainWindow,

    /// <summary>
    /// A Win32 call returned an error (most commonly Windows refusing to
    /// hand off focus). The window may have flashed in the taskbar instead.
    /// </summary>
    Win32Failure,
}

/// <summary>
/// Brings the top-level window of a previously-launched <c>pwsh.exe</c>
/// process to the foreground so a second click on the same session card
/// activates the existing terminal instead of spawning a duplicate (#104).
/// </summary>
/// <remarks>
/// The default implementation in
/// <see cref="ProcessWindowActivator"/> uses the standard Win32 sequence:
/// <c>AllowSetForegroundWindow → ShowWindowAsync(SW_RESTORE) →
/// SetForegroundWindow</c>. Windows may still refuse focus theft when CSM
/// is not the foreground process; in that case the activation flashes the
/// taskbar button and the caller treats it as success.
/// </remarks>
public interface IWindowActivator
{
    /// <summary>
    /// Brings the top-level window of the process with PID
    /// <paramref name="processId"/> to the foreground. Returns the outcome
    /// so the caller can decide whether to re-launch.
    /// </summary>
    WindowActivationResult Activate(int processId);
}
