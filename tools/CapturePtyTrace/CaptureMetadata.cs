using System;
using System.Text.Json.Serialization;

namespace CopilotSessionManager.Tools.CapturePtyTrace;

/// <summary>
/// JSON sidecar that accompanies a captured trace. Records the geometry,
/// the command line, and timing so the conformance harness (Phase 2D)
/// can replay against the same conditions the trace was captured under.
/// </summary>
internal sealed record CaptureMetadata(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("commandLine")] string CommandLine,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory,
    [property: JsonPropertyName("columns")] short Columns,
    [property: JsonPropertyName("rows")] short Rows,
    [property: JsonPropertyName("capturedAtUtc")] DateTime CapturedAtUtc,
    [property: JsonPropertyName("durationMs")] long DurationMilliseconds,
    [property: JsonPropertyName("bytesCaptured")] long BytesCaptured,
    [property: JsonPropertyName("traceFile")] string TraceFile)
{
    public const string CurrentSchema = "csm.capture-pty-trace.v1";

    public static CaptureMetadata Create(
        string commandLine, string? workingDirectory, short columns, short rows,
        DateTime capturedAtUtc, TimeSpan duration, long bytesCaptured, string traceFile) =>
        new(CurrentSchema, commandLine, workingDirectory, columns, rows,
            capturedAtUtc, (long)duration.TotalMilliseconds, bytesCaptured, traceFile);
}
