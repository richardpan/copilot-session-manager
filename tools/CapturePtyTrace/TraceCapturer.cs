using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CopilotSessionManager.Native;

namespace CopilotSessionManager.Tools.CapturePtyTrace;

/// <summary>
/// Spawns a child process under a <see cref="PseudoConsole"/> and writes
/// every byte the child emits to a binary trace file, plus a JSON
/// metadata sidecar. Phase 2C of epic #93.
/// </summary>
internal sealed class TraceCapturer
{
    private const int ReadBufferSize = 4096;
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
    };

    public CaptureResult Capture(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var capturedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))
            ?? Directory.GetCurrentDirectory());

        long bytesCaptured = 0;

        using (var pty = PseudoConsole.Start(
                   request.CommandLine,
                   request.Columns,
                   request.Rows,
                   request.WorkingDirectory))
        using (var traceStream = new FileStream(request.OutputPath, FileMode.Create, FileAccess.Write))
        {
            // ConPTY holds the write end of the output pipe even after the
            // child process exits, so a blocking Read on this thread will not
            // return on its own. A watchdog thread waits for the child to
            // exit (with a small post-exit grace period to flush ConPTY's
            // internal buffer) and then disposes the PseudoConsole, which
            // closes the pipe and unblocks the read loop here.
            var watchdog = new Thread(() =>
            {
                while (!pty.HasExited && !cancellationToken.IsCancellationRequested)
                {
                    Thread.Sleep(25);
                }
                Thread.Sleep(request.PostExitDrainMs);
                try
                { pty.Dispose(); }
                catch { /* best effort */ }
            })
            { IsBackground = true, Name = "CapturePtyTrace.Watchdog" };
            watchdog.Start();

            var output = pty.OutputStream;
            var buffer = new byte[ReadBufferSize];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var read = output.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    traceStream.Write(buffer, 0, read);
                    bytesCaptured += read;

                    if (request.MirrorToStdout)
                    {
                        using var stdout = Console.OpenStandardOutput();
                        stdout.Write(buffer, 0, read);
                    }
                }
            }
            catch (IOException) { /* pipe closed at watchdog dispose */ }
            catch (ObjectDisposedException) { /* stream torn down at dispose */ }

            watchdog.Join(TimeSpan.FromSeconds(2));
            traceStream.Flush();
        }

        stopwatch.Stop();

        var metadata = CaptureMetadata.Create(
            request.CommandLine,
            request.WorkingDirectory,
            request.Columns,
            request.Rows,
            capturedAt,
            stopwatch.Elapsed,
            bytesCaptured,
            Path.GetFileName(request.OutputPath));

        var metadataPath = request.MetadataPath
            ?? Path.ChangeExtension(request.OutputPath, ".json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, s_jsonOptions));

        return new CaptureResult(request.OutputPath, metadataPath, bytesCaptured, stopwatch.Elapsed);
    }
}

internal sealed record CaptureRequest(
    string CommandLine,
    string OutputPath,
    short Columns = 120,
    short Rows = 30,
    string? WorkingDirectory = null,
    string? MetadataPath = null,
    bool MirrorToStdout = false,
    int PostExitDrainMs = 500);

internal sealed record CaptureResult(
    string TracePath,
    string MetadataPath,
    long BytesCaptured,
    TimeSpan Duration);
