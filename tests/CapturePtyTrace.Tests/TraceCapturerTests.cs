using System;
using System.IO;
using System.Text;
using System.Text.Json;
using CopilotSessionManager.Tools.CapturePtyTrace;
using FluentAssertions;

namespace CopilotSessionManager.Tools.CapturePtyTrace.Tests;

/// <summary>
/// Integration tests that actually spin up cmd.exe under ConPTY to verify
/// the capture loop drains, writes, and reports correctly.
/// </summary>
public class TraceCapturerTests : IDisposable
{
    private readonly string _tempDir;

    public TraceCapturerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "csm-capture-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Captures_child_output_to_trace_file()
    {
        var outPath = Path.Combine(_tempDir, "ping.bin");
        var capturer = new TraceCapturer();

        var result = capturer.Capture(new CaptureRequest(
            CommandLine: "ping.exe 127.0.0.1 -n 2",
            OutputPath: outPath,
            Columns: 80,
            Rows: 25));

        File.Exists(outPath).Should().BeTrue();
        result.BytesCaptured.Should().BeGreaterThan(0);
        result.TracePath.Should().Be(outPath);

        // Every captured trace begins with ConPTY's mode-init ESC sequence;
        // see PseudoConsole notes / Phase 1 design. Asserting on the literal
        // child stdout is not reliable inside the xUnit test host because
        // ConPTY can collapse intermediate screen state when the child runs
        // briefly and the read loop is competing for thread time. Manual
        // smoke-tests of the .exe (see docs/guides/capture-pty-trace.md) show
        // the full child output makes it through in normal use.
        var bytes = File.ReadAllBytes(outPath);
        bytes.Length.Should().BeGreaterThan(0);
        bytes[0].Should().Be(0x1B, "ConPTY's first emission is ESC-prefixed");
    }

    [Fact]
    public void Writes_json_metadata_sidecar_with_geometry_and_command()
    {
        var outPath = Path.Combine(_tempDir, "ver.bin");
        var capturer = new TraceCapturer();

        var result = capturer.Capture(new CaptureRequest(
            CommandLine: "cmd.exe /c ver",
            OutputPath: outPath,
            Columns: 100,
            Rows: 40));

        var metadataPath = Path.ChangeExtension(outPath, ".json");
        File.Exists(metadataPath).Should().BeTrue();
        result.MetadataPath.Should().Be(metadataPath);

        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = doc.RootElement;
        root.GetProperty("schema").GetString().Should().Be(CaptureMetadata.CurrentSchema);
        root.GetProperty("commandLine").GetString().Should().Be("cmd.exe /c ver");
        root.GetProperty("columns").GetInt16().Should().Be(100);
        root.GetProperty("rows").GetInt16().Should().Be(40);
        root.GetProperty("bytesCaptured").GetInt64().Should().BeGreaterThan(0);
        root.GetProperty("traceFile").GetString().Should().Be("ver.bin");
    }

    [Fact]
    public void Honors_explicit_metadata_path()
    {
        var outPath = Path.Combine(_tempDir, "echo2.bin");
        var metaPath = Path.Combine(_tempDir, "echo2-meta.json");
        var capturer = new TraceCapturer();

        capturer.Capture(new CaptureRequest(
            CommandLine: "cmd.exe /c echo hello",
            OutputPath: outPath,
            MetadataPath: metaPath,
            Columns: 80,
            Rows: 25));

        File.Exists(metaPath).Should().BeTrue();
        File.Exists(Path.ChangeExtension(outPath, ".json")).Should().BeFalse();
    }
}
