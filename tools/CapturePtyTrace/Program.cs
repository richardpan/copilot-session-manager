using System;
using System.IO;

namespace CopilotSessionManager.Tools.CapturePtyTrace;

/// <summary>
/// CLI entry point for the trace capture tool. The implementation lives
/// in <see cref="TraceCapturer"/> so the IO loop can be unit-tested
/// independently of the argument parser.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = CommandLineParser.Parse(args);
            if (options is null)
            {
                PrintUsage(Console.Out);
                return 1;
            }

            var capturer = new TraceCapturer();
            var result = capturer.Capture(new CaptureRequest(
                CommandLine: options.CommandLine,
                OutputPath: options.OutputPath,
                Columns: options.Columns,
                Rows: options.Rows,
                WorkingDirectory: options.WorkingDirectory,
                MirrorToStdout: options.Mirror));

            Console.Error.WriteLine(
                $"captured {result.BytesCaptured:N0} bytes in {result.Duration.TotalMilliseconds:N0} ms");
            Console.Error.WriteLine($"  trace:    {result.TracePath}");
            Console.Error.WriteLine($"  metadata: {result.MetadataPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"capture-pty-trace: {ex.Message}");
            return 2;
        }
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("usage: CapturePtyTrace [options] -- <command line...>");
        writer.WriteLine();
        writer.WriteLine("options:");
        writer.WriteLine("  --out <path>      output trace file (default: trace-<ts>.bin in cwd)");
        writer.WriteLine("  --metadata <path> JSON sidecar (default: <out>.json)");
        writer.WriteLine("  --cols <n>        ConPTY columns (default: 120)");
        writer.WriteLine("  --rows <n>        ConPTY rows    (default: 30)");
        writer.WriteLine("  --cwd <path>      working directory for the child");
        writer.WriteLine("  --mirror          also write captured bytes to this process's stdout");
        writer.WriteLine();
        writer.WriteLine("example:");
        writer.WriteLine("  CapturePtyTrace --cols 100 --out pwsh.trace.bin -- pwsh -NoLogo -Command \"Get-ChildItem\"");
    }
}
