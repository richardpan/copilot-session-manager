using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CopilotSessionManager.Core.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotSessionManager.Core.Tests.Logging;

public class ZipLogBundlerTests : IDisposable
{
    private readonly string _logsDir;
    private readonly string _outDir;

    public ZipLogBundlerTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "csm-bundle-" + Guid.NewGuid().ToString("N"));
        _logsDir = Path.Combine(root, "logs");
        _outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(_logsDir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        try
        { Directory.Delete(Path.GetDirectoryName(_logsDir)!, recursive: true); }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private ZipLogBundler Sut() =>
        new(_logsDir, NullLogger<ZipLogBundler>.Instance);

    [Fact]
    public async Task BundleAsync_EmptyLogsDir_ProducesZipWithJustManifest()
    {
        var dst = Path.Combine(_outDir, "bundle.zip");
        var result = await Sut().BundleAsync(dst);

        result.FileCount.Should().Be(0);
        File.Exists(dst).Should().BeTrue();
        result.TotalBytes.Should().BeGreaterThan(0);

        using var zip = ZipFile.OpenRead(dst);
        zip.Entries.Should().ContainSingle().Which.FullName.Should().Be("manifest.txt");
    }

    [Fact]
    public async Task BundleAsync_IncludesAllLogFiles_UnderLogsFolder()
    {
        await File.WriteAllTextAsync(Path.Combine(_logsDir, "app-2026-05-08.log"), "line1\nline2\n");
        await File.WriteAllTextAsync(Path.Combine(_logsDir, "app-2026-05-09.log"), "lineA\nlineB\n");
        await File.WriteAllTextAsync(Path.Combine(_logsDir, "ignore.txt"), "should not be included");

        var dst = Path.Combine(_outDir, "bundle.zip");
        var result = await Sut().BundleAsync(dst);

        result.FileCount.Should().Be(2);

        using var zip = ZipFile.OpenRead(dst);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(new[]
        {
            "manifest.txt",
            "logs/app-2026-05-08.log",
            "logs/app-2026-05-09.log",
        });
    }

    [Fact]
    public async Task BundleAsync_ManifestContainsExpectedHeaders()
    {
        var dst = Path.Combine(_outDir, "bundle.zip");
        await Sut().BundleAsync(dst);

        using var zip = ZipFile.OpenRead(dst);
        var manifest = zip.GetEntry("manifest.txt");
        manifest.Should().NotBeNull();
        using var sr = new StreamReader(manifest!.Open());
        var text = await sr.ReadToEndAsync();
        text.Should().Contain("Copilot Session Manager log bundle");
        text.Should().Contain("App version:");
        text.Should().Contain("OS:");
        text.Should().Contain("redacted at write time");
    }

    [Fact]
    public async Task BundleAsync_OverwritesExistingFile()
    {
        var dst = Path.Combine(_outDir, "bundle.zip");
        await File.WriteAllTextAsync(dst, "stale-existing-content");

        await File.WriteAllTextAsync(Path.Combine(_logsDir, "app.log"), "first");
        var first = await Sut().BundleAsync(dst);

        await File.WriteAllTextAsync(Path.Combine(_logsDir, "app2.log"), "second");
        var second = await Sut().BundleAsync(dst);

        second.FileCount.Should().Be(2);
        File.Exists(dst + ".tmp").Should().BeFalse();
        first.DestinationPath.Should().Be(dst);
    }

    [Fact]
    public async Task BundleAsync_MissingLogsDir_StillSucceeds()
    {
        Directory.Delete(_logsDir, recursive: true);
        var dst = Path.Combine(_outDir, "bundle.zip");

        var result = await Sut().BundleAsync(dst);

        result.FileCount.Should().Be(0);
        File.Exists(dst).Should().BeTrue();
    }

    [Fact]
    public void Constructor_RejectsBlankLogsDir()
    {
        FluentActions.Invoking(() => new ZipLogBundler("", NullLogger<ZipLogBundler>.Instance))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task BundleAsync_RejectsBlankDestination()
    {
        await FluentActions.Invoking(() => Sut().BundleAsync(""))
            .Should().ThrowAsync<ArgumentException>();
    }
}
