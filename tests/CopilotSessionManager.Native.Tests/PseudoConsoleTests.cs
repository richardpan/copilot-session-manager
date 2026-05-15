using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Native.Tests;

/// <summary>
/// Integration tests for <see cref="PseudoConsole"/>. These tests actually
/// spawn child processes via ConPTY; they're only meaningful on Windows. The
/// project's TFM is <c>net8.0-windows</c> so on other OSes they simply won't
/// build, no need to gate explicitly.
/// </summary>
public class PseudoConsoleTests
{
    private const int ReadTimeoutMs = 5_000;

    [Fact]
    public async Task Start_SpawnsChild_AndOutputPipeYieldsConPtyInitBytes()
    {
        var pty = PseudoConsole.Start("cmd.exe", cols: 120, rows: 30);
        try
        {
            pty.ProcessId.Should().BeGreaterThan(0);

            var firstBytes = new byte[64];
            var readTask = Task.Run(() => pty.OutputStream.Read(firstBytes, 0, firstBytes.Length));

            var completed = await Task.WhenAny(readTask, Task.Delay(ReadTimeoutMs));
            completed.Should().BeSameAs(readTask, "ConPTY should push its mode-init sequence promptly");

            var bytesRead = await readTask;
            bytesRead.Should().BeGreaterThan(0, "the output pipe should yield ConPTY's initial VT sequence");
            firstBytes[0].Should().Be(0x1B, "ConPTY's first emission is an ESC-prefixed mode-set sequence");
        }
        finally
        {
            pty.Dispose();
        }
    }

    [Fact]
    public void Resize_WhileChildRunning_DoesNotThrow()
    {
        using var pty = PseudoConsole.Start("cmd.exe", cols: 80, rows: 24);

        var act1 = () => pty.Resize(120, 30);
        var act2 = () => pty.Resize(40, 10);
        var act3 = () => pty.Resize(200, 60);

        act1.Should().NotThrow();
        act2.Should().NotThrow();
        act3.Should().NotThrow();
    }

    [Fact]
    public void Dispose_ClosesHandles_AndIsIdempotent()
    {
        var pty = PseudoConsole.Start("cmd.exe", cols: 80, rows: 24);
        var pid = pty.ProcessId;

        pty.Dispose();

        var process = SafeGetProcessById(pid);
        if (process is not null)
        {
            using (process)
            {
                // The child should have terminated quickly. We give it a generous
                // grace period to avoid flakes on slow CI machines.
                process.WaitForExit(5_000).Should().BeTrue(
                    "Disposing the PseudoConsole should terminate the child process.");
            }
        }

        var doubleDispose = () => pty.Dispose();
        doubleDispose.Should().NotThrow();
    }

    [Fact]
    public void Dispose_AfterUse_DisposesStreams()
    {
        var pty = PseudoConsole.Start("cmd.exe /c echo done", cols: 80, rows: 24);
        var input = pty.InputStream;
        var output = pty.OutputStream;

        pty.Dispose();

        var inputAccess = () => input.WriteByte(0x20);
        var outputAccess = () => output.ReadByte();

        inputAccess.Should().Throw<ObjectDisposedException>();
        outputAccess.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Start_InvalidCommandLine_ThrowsWin32Exception()
    {
        var act = () => PseudoConsole.Start("this-command-does-not-exist-9f2a.exe", cols: 80, rows: 24);

        act.Should().Throw<Win32Exception>();
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(-1, 24)]
    [InlineData(80, -1)]
    public void Start_NonPositiveDimensions_Throws(short cols, short rows)
    {
        var act = () => PseudoConsole.Start("cmd.exe", cols, rows);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Start_EmptyCommandLine_Throws()
    {
        var act = () => PseudoConsole.Start("   ", cols: 80, rows: 24);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resize_AfterDispose_ThrowsObjectDisposed()
    {
        var pty = PseudoConsole.Start("cmd.exe", cols: 80, rows: 24);
        pty.Dispose();

        var act = () => pty.Resize(100, 30);

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void HasExited_AfterShortCommand_BecomesTrue()
    {
        using var pty = PseudoConsole.Start("cmd.exe /c exit 0", cols: 80, rows: 24);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!pty.HasExited && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        pty.HasExited.Should().BeTrue();
    }

    private static System.Diagnostics.Process? SafeGetProcessById(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // Process already gone; that's the success path for this test.
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
