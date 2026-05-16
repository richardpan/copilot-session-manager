using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Tests.Conformance;

/// <summary>
/// Replays each <c>.trace.bin</c> file under <c>samples/traces/</c>
/// through <see cref="VtParser"/> + <see cref="ScreenBuffer"/> and
/// snapshot-diffs the result against a committed
/// <c>.snapshot.txt</c> sibling file. Phase 2D of epic #93 — closes
/// #164 by locking down what the parser + buffer actually do on real
/// ConPTY output.
/// </summary>
/// <remarks>
/// To regenerate the snapshot files (e.g. after a deliberate
/// behavior change), set the environment variable
/// <c>CSM_REGEN_SNAPSHOTS=1</c> and re-run the tests. The tests will
/// write the actual snapshot to the expected file and pass — review
/// the diff in <c>git diff samples/traces/</c> before committing.
/// </remarks>
public class TraceConformanceTests
{
    private const string RegenerationEnvVar = "CSM_REGEN_SNAPSHOTS";

    public static IEnumerable<object[]> AllTraces()
    {
        var dir = RepoLocator.SamplesTracesDir();
        if (!Directory.Exists(dir))
        {
            yield break;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.trace.bin").OrderBy(f => f, StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetFileName(file) };
        }
    }

    [Fact]
    public void At_least_one_captured_trace_is_committed()
    {
        AllTraces().Should().NotBeEmpty(
            "Phase 2D's conformance bar depends on real captured traces under samples/traces/. See docs/guides/capture-pty-trace.md.");
    }

    [Theory]
    [MemberData(nameof(AllTraces))]
    public void Trace_replays_to_committed_snapshot(string traceFileName)
    {
        var samplesDir = RepoLocator.SamplesTracesDir();
        var tracePath = Path.Combine(samplesDir, traceFileName);
        var metadataPath = Path.ChangeExtension(tracePath, ".json");
        var snapshotPath = Path.Combine(samplesDir, Path.GetFileNameWithoutExtension(traceFileName) + ".snapshot.txt");

        File.Exists(tracePath).Should().BeTrue($"trace file should exist at {tracePath}");
        File.Exists(metadataPath).Should().BeTrue($"metadata sidecar should exist at {metadataPath}");

        var metadata = LoadMetadata(metadataPath);
        var bytes = File.ReadAllBytes(tracePath);

        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(bytes);

        var buffer = new ScreenBuffer(metadata.Rows, metadata.Columns);
        buffer.ApplyAll(events);

        var actual = SnapshotBuilder.Build(traceFileName, metadata, buffer, events);

        var regenerate = string.Equals(
            Environment.GetEnvironmentVariable(RegenerationEnvVar),
            "1", StringComparison.Ordinal);

        if (regenerate || !File.Exists(snapshotPath))
        {
            File.WriteAllText(snapshotPath, actual);
            if (!regenerate)
            {
                // First-time write — fail loudly so the contributor knows
                // to inspect the produced snapshot and commit it.
                throw new Xunit.Sdk.XunitException(
                    $"No snapshot file existed at {snapshotPath}; one has now been written. " +
                    "Inspect it and commit it to lock in this trace's conformance.");
            }
            return;
        }

        var expected = File.ReadAllText(snapshotPath);
        actual.Should().Be(
            expected,
            "trace {0} should still replay to its committed snapshot. " +
            "If this change is deliberate, regenerate with CSM_REGEN_SNAPSHOTS=1.",
            traceFileName);
    }

    private static TraceMetadata LoadMetadata(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        return new TraceMetadata(
            CommandLine: root.GetProperty("commandLine").GetString() ?? string.Empty,
            Columns: root.GetProperty("columns").GetInt16(),
            Rows: root.GetProperty("rows").GetInt16());
    }
}
