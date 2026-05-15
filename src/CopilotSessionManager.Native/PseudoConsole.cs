using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopilotSessionManager.Native;

/// <summary>
/// Owns a Windows pseudo-console (ConPTY) and a child process attached to it.
/// Phase 1 of epic #93 (issue #160): the foundation that later phases — a VT
/// parser, a WPF terminal control, multi-tab UI — build on.
/// </summary>
/// <remarks>
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item>Caller invokes <see cref="Start(string, short, short, string?)"/>.</item>
///   <item>Caller reads/writes the child via <see cref="OutputStream"/> /
///   <see cref="InputStream"/>.</item>
///   <item>Caller may call <see cref="Resize(short, short)"/> when its UI
///   surface changes.</item>
///   <item>Caller invokes <see cref="Dispose"/>, which closes the pseudo-console,
///   the streams, the attribute list, and the child handles. Disposing while
///   the child is still running terminates it.</item>
/// </list>
/// <para>This class is not thread-safe; the host control is expected to
/// own a single instance and route reads/writes through one queue.</para>
/// </remarks>
public sealed class PseudoConsole : IDisposable
{
    private IntPtr _hPC;
    private IntPtr _attributeList;
    private SafeFileHandle? _inputWrite;
    private SafeFileHandle? _outputRead;
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private bool _disposed;

    private PseudoConsole(
        IntPtr hPC,
        IntPtr attributeList,
        SafeFileHandle inputWrite,
        SafeFileHandle outputRead,
        IntPtr hProcess,
        IntPtr hThread,
        int processId)
    {
        _hPC = hPC;
        _attributeList = attributeList;
        _inputWrite = inputWrite;
        _outputRead = outputRead;
        _hProcess = hProcess;
        _hThread = hThread;
        ProcessId = processId;

        _inputStream = new FileStream(_inputWrite, FileAccess.Write, bufferSize: 4096, isAsync: false);
        _outputStream = new FileStream(_outputRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    /// <summary>Write-side stream into the child's stdin.</summary>
    public Stream InputStream => _inputStream ?? throw new ObjectDisposedException(nameof(PseudoConsole));

    /// <summary>Read-side stream of the child's stdout/stderr (interleaved by ConPTY with VT escapes).</summary>
    public Stream OutputStream => _outputStream ?? throw new ObjectDisposedException(nameof(PseudoConsole));

    /// <summary>Child process id.</summary>
    public int ProcessId { get; }

    /// <summary>True once the child process has exited.</summary>
    public bool HasExited
    {
        get
        {
            if (_hProcess == IntPtr.Zero)
            {
                return true;
            }

            if (!NativeMethods.GetExitCodeProcess(_hProcess, out var exit))
            {
                return true;
            }

            return exit != NativeMethods.STILL_ACTIVE;
        }
    }

    /// <summary>
    /// Spawns <paramref name="commandLine"/> attached to a freshly created
    /// pseudo-console of <paramref name="cols"/> × <paramref name="rows"/>.
    /// </summary>
    /// <param name="commandLine">
    /// Command line as it would be passed to <c>CreateProcess</c> (e.g.
    /// <c>"cmd.exe /c echo hi"</c>). Per Win32, the buffer is writable —
    /// we hand a fresh string to the marshaller every time.
    /// </param>
    /// <param name="cols">Initial column count. Must be &gt; 0.</param>
    /// <param name="rows">Initial row count. Must be &gt; 0.</param>
    /// <param name="workingDirectory">
    /// Optional working directory; defaults to the host process's cwd.
    /// </param>
    /// <exception cref="ArgumentException">Bad arguments.</exception>
    /// <exception cref="Win32Exception">Any underlying Win32 failure.</exception>
    public static PseudoConsole Start(string commandLine, short cols, short rows, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line is required.", nameof(commandLine));
        }

        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "Must be > 0.");
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Must be > 0.");
        }

        IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero;
        IntPtr hPC = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        var attributeListAllocated = false;
        var pi = default(NativeMethods.PROCESS_INFORMATION);
        var processCreated = false;

        try
        {
            if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed.");
            }

            if (!NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed.");
            }

            var size = new NativeMethods.COORD { X = cols, Y = rows };
            var hr = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out hPC);
            if (hr != 0)
            {
                throw new Win32Exception(hr, $"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");
            }

            // ConPTY now owns its ends of the pipes; close ours so EOF
            // propagates when the child exits.
            NativeMethods.CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            NativeMethods.CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;

            attributeList = AllocateAttributeList(hPC);
            attributeListAllocated = true;

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>(),
                },
                lpAttributeList = attributeList,
            };

            // CreateProcess may mutate its command-line argument internally,
            // but the default marshaller hands it a writable native copy of
            // our string and frees it on return — we don't need to defensively
            // duplicate the managed string ourselves.
            if (!NativeMethods.CreateProcess(
                    lpApplicationName: null,
                    lpCommandLine: commandLine,
                    lpProcessAttributes: IntPtr.Zero,
                    lpThreadAttributes: IntPtr.Zero,
                    bInheritHandles: false,
                    dwCreationFlags: NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
                    lpEnvironment: IntPtr.Zero,
                    lpCurrentDirectory: workingDirectory,
                    lpStartupInfo: ref startupInfo,
                    lpProcessInformation: out pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for command line: {commandLine}");
            }

            processCreated = true;

            var inputWriteHandle = new SafeFileHandle(inputWrite, ownsHandle: true);
            var outputReadHandle = new SafeFileHandle(outputRead, ownsHandle: true);
            inputWrite = IntPtr.Zero;
            outputRead = IntPtr.Zero;

            var console = new PseudoConsole(
                hPC,
                attributeList,
                inputWriteHandle,
                outputReadHandle,
                pi.hProcess,
                pi.hThread,
                pi.dwProcessId);

            // Ownership transferred; suppress cleanup paths below.
            hPC = IntPtr.Zero;
            attributeList = IntPtr.Zero;
            attributeListAllocated = false;
            processCreated = false;

            return console;
        }
        catch
        {
            if (processCreated)
            {
                NativeMethods.TerminateProcess(pi.hProcess, 1);
                NativeMethods.CloseHandle(pi.hThread);
                NativeMethods.CloseHandle(pi.hProcess);
            }

            if (attributeListAllocated && attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (hPC != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(hPC);
            }

            if (inputRead != IntPtr.Zero)
                NativeMethods.CloseHandle(inputRead);
            if (inputWrite != IntPtr.Zero)
                NativeMethods.CloseHandle(inputWrite);
            if (outputRead != IntPtr.Zero)
                NativeMethods.CloseHandle(outputRead);
            if (outputWrite != IntPtr.Zero)
                NativeMethods.CloseHandle(outputWrite);

            throw;
        }
    }

    /// <summary>Resize the underlying ConPTY. Safe to call before / after the child exits.</summary>
    /// <exception cref="ObjectDisposedException">Already disposed.</exception>
    /// <exception cref="Win32Exception">ConPTY refused the resize.</exception>
    public void Resize(short cols, short rows)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PseudoConsole));
        }

        if (cols <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cols), cols, "Must be > 0.");
        }

        if (rows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "Must be > 0.");
        }

        var hr = NativeMethods.ResizePseudoConsole(_hPC, new NativeMethods.COORD { X = cols, Y = rows });
        if (hr != 0)
        {
            throw new Win32Exception(hr, $"ResizePseudoConsole failed (HRESULT 0x{hr:X8}).");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Closing the pseudo-console first signals EOF to the child and lets
        // ConPTY flush its buffered screen state into our output pipe before
        // we tear anything else down.
        if (_hPC != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        // Closing the input stream signals EOF to the child stdin.
        _inputStream?.Dispose();
        _inputStream = null;
        _inputWrite = null;

        _outputStream?.Dispose();
        _outputStream = null;
        _outputRead = null;

        if (_hProcess != IntPtr.Zero)
        {
            // Best-effort terminate so we don't leak a runaway child.
            if (NativeMethods.GetExitCodeProcess(_hProcess, out var code) && code == NativeMethods.STILL_ACTIVE)
            {
                NativeMethods.TerminateProcess(_hProcess, 1);
            }

            NativeMethods.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }

        if (_hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_hThread);
            _hThread = IntPtr.Zero;
        }

        if (_attributeList != IntPtr.Zero)
        {
            NativeMethods.DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Allocate and initialise a process-thread attribute list with a single
    /// entry binding <paramref name="hPC"/> via
    /// <see cref="NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE"/>.
    /// </summary>
    /// <remarks>
    /// We deliberately hold onto <paramref name="hPC"/> as a struct field on
    /// <see cref="PseudoConsole"/> so that the address used by
    /// <c>UpdateProcThreadAttribute</c> (which points into the caller's stack
    /// during the call) is safe — <c>UpdateProcThreadAttribute</c> records
    /// the value, not the pointer, into the attribute list. See
    /// learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute.
    /// </remarks>
    private static IntPtr AllocateAttributeList(IntPtr hPC)
    {
        var size = IntPtr.Zero;

        // First call gets the size; expected to fail with ERROR_INSUFFICIENT_BUFFER.
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList (size query) failed.");
        }

        var list = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    list,
                    0,
                    (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                NativeMethods.DeleteProcThreadAttributeList(list);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute (PSEUDOCONSOLE) failed.");
            }

            return list;
        }
        catch
        {
            Marshal.FreeHGlobal(list);
            throw;
        }
    }
}
