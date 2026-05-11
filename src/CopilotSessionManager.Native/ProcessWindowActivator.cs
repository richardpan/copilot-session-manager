using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CopilotSessionManager.Native;

/// <summary>
/// Default <see cref="IWindowActivator"/> backed by Win32 P/Invoke against
/// <c>user32.dll</c>. Looks up <c>Process.MainWindowHandle</c> for the
/// supplied PID and runs the documented restore-and-foreground sequence.
/// </summary>
public sealed class ProcessWindowActivator : IWindowActivator
{
    private const int SwRestore = 9;
    private const int SwShow = 5;

    public WindowActivationResult Activate(int processId)
    {
        if (processId <= 0)
        {
            return WindowActivationResult.ProcessNotRunning;
        }

        Process? process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return WindowActivationResult.ProcessNotRunning;
        }
        catch (InvalidOperationException)
        {
            return WindowActivationResult.ProcessNotRunning;
        }

        try
        {
            if (process.HasExited)
            {
                return WindowActivationResult.ProcessNotRunning;
            }

            // Refresh once so MainWindowHandle reflects the current state
            // (it is cached per-Process instance).
            process.Refresh();
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
            {
                return WindowActivationResult.NoMainWindow;
            }

            // Best-effort: clear the focus-steal restriction by giving the
            // target process explicit permission to take the foreground.
            // Failure here is non-fatal — SetForegroundWindow still tries.
            try
            {
                _ = AllowSetForegroundWindow(processId);
            }
            catch
            {
                // user32 may not export this on every TFM; ignore.
            }

            if (IsIconic(hwnd))
            {
                _ = ShowWindowAsync(hwnd, SwRestore);
            }
            else
            {
                _ = ShowWindowAsync(hwnd, SwShow);
            }

            return SetForegroundWindow(hwnd)
                ? WindowActivationResult.Activated
                : WindowActivationResult.Win32Failure;
        }
        finally
        {
            process.Dispose();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
