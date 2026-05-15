using System;
using System.Runtime.InteropServices;

namespace CopilotSessionManager.Native;

/// <summary>
/// Win32 P/Invoke declarations for the ConPTY pseudo-console plumbing
/// (epic #93, Phase 1 — issue #160). The minimum surface needed to:
/// <list type="bullet">
///   <item>Allocate an <c>HPCON</c> bound to a pair of anonymous pipes via
///   <c>CreatePseudoConsole</c>.</item>
///   <item>Spawn a child process with that pseudo-console attached via
///   <c>STARTUPINFOEX</c> + <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c>.</item>
///   <item>Resize the console as the host control changes size.</item>
///   <item>Tear everything down cleanly.</item>
/// </list>
/// Internal because the only consumer is <see cref="PseudoConsole"/>; tests
/// reach in via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class NativeMethods
{
    // ---- Constants ----

    /// <summary>
    /// Attribute identifier used with <see cref="UpdateProcThreadAttribute"/>
    /// to bind a pseudo-console handle (<c>HPCON</c>) to a child process via
    /// <see cref="STARTUPINFOEX.lpAttributeList"/>. Documented at
    /// learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute.
    /// </summary>
    public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    /// <summary>
    /// <c>CreateProcess</c> creation flag indicating that the
    /// <c>STARTUPINFO</c> argument is really a <c>STARTUPINFOEX</c>.
    /// </summary>
    public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;

    /// <summary>
    /// Returned by <c>WaitForSingleObject</c> when the wait completes
    /// because the object became signaled. Used here to poll process exit.
    /// </summary>
    public const uint WAIT_OBJECT_0 = 0;

    /// <summary>Sentinel used by <c>WaitForSingleObject</c>.</summary>
    public const uint WAIT_TIMEOUT = 0x00000102;

    // ---- Structs ----

    /// <summary>Win32 <c>COORD</c> — short X, short Y. Matches kernel32 layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct COORD
    {
        public short X;
        public short Y;
    }

    /// <summary>
    /// Win32 <c>SECURITY_ATTRIBUTES</c>. Always passed as a zero-initialised
    /// pointer to <c>CreatePipe</c> in our usage; included for completeness.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    /// <summary>Win32 <c>STARTUPINFO</c>. 18 fields, exact order matters.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    /// <summary>
    /// Win32 <c>STARTUPINFOEX</c> — <see cref="STARTUPINFO"/> followed by a
    /// pointer to a process-thread attribute list. <c>cb</c> must be set to
    /// <c>sizeof(STARTUPINFOEX)</c> when passed to <c>CreateProcess</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    /// <summary>Win32 <c>PROCESS_INFORMATION</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // ---- P/Invoke ----

    /// <summary>
    /// Creates a new pseudo-console of the given <paramref name="size"/> and
    /// binds it to the supplied input/output pipe handles. The returned
    /// <paramref name="hPC"/> must be released via
    /// <see cref="ClosePseudoConsole"/> and cleaned up before the input/output
    /// handles are closed. Returns an <c>HRESULT</c>; non-zero is failure.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = false, ExactSpelling = true)]
    public static extern int CreatePseudoConsole(
        COORD size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr hPC);

    /// <summary>Resizes an existing pseudo-console. Returns an HRESULT.</summary>
    [DllImport("kernel32.dll", SetLastError = false, ExactSpelling = true)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    /// <summary>Closes a pseudo-console handle. No return value.</summary>
    [DllImport("kernel32.dll", SetLastError = false, ExactSpelling = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    /// <summary>
    /// Creates a pair of anonymous pipe handles. <paramref name="hReadPipe"/>
    /// receives the read side; <paramref name="hWritePipe"/> receives the
    /// write side.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    /// <summary>Closes a kernel handle.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// First call (with <paramref name="lpAttributeList"/> == <c>IntPtr.Zero</c>)
    /// returns the required buffer size via <paramref name="lpSize"/>. Second
    /// call (after allocating that many bytes) initialises the list.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    /// <summary>Attaches an attribute (e.g. an <c>HPCON</c>) to an attribute list.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    /// <summary>Releases the resources held by an attribute list.</summary>
    [DllImport("kernel32.dll", SetLastError = false)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    /// <summary>
    /// Creates a process. The pseudo-console flow always passes
    /// <see cref="EXTENDED_STARTUPINFO_PRESENT"/> and a <see cref="STARTUPINFOEX"/>
    /// whose attribute list has <see cref="PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE"/>
    /// set to the <c>HPCON</c> returned by <see cref="CreatePseudoConsole"/>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    /// <summary>Waits for a kernel object (process / thread / event) to become signaled.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>Retrieves the termination status of a process.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    /// <summary>Forcibly terminates a process with the given exit code.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>STILL_ACTIVE sentinel returned by <see cref="GetExitCodeProcess"/> when running.</summary>
    public const int STILL_ACTIVE = 259;
}
