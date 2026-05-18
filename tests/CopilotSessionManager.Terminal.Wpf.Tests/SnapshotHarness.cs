using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CopilotSessionManager.Terminal;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

/// <summary>
/// Issue #181: render a deterministic TerminalControl scene to a PNG and
/// compare against a baseline committed under
/// <c>tests/CopilotSessionManager.Terminal.Wpf.Tests/baselines/</c>.
///
/// Comparison is tolerant: per-pixel channel diff up to
/// <see cref="MaxChannelDiff"/> is ignored, and up to
/// <see cref="MaxDiffPixelRatio"/> of all pixels may exceed that bound
/// before the check fails. This forgives small font-hinting differences
/// between dev machines and CI without losing the ability to flag real
/// visual regressions (palette shifts, wrong glyphs, missing background
/// rects).
///
/// Set <c>REGEN_BASELINES=1</c> to overwrite the committed baseline with
/// the freshly rendered PNG. Failures write the actual + diff PNGs into
/// <c>TestResults/snapshots/</c> next to the test assembly.
/// </summary>
internal static class SnapshotHarness
{
    /// <summary>Allowed absolute diff per BGRA channel (0..255).</summary>
    public const int MaxChannelDiff = 16;

    /// <summary>Maximum fraction of pixels that may exceed <see cref="MaxChannelDiff"/>.</summary>
    public const double MaxDiffPixelRatio = 0.01;

    /// <summary>Font family list used by every snapshot.</summary>
    public const string SnapshotFontFamily = "Cascadia Mono, Consolas, Courier New";

    /// <summary>Font em-size used by every snapshot.</summary>
    public const double SnapshotFontSize = 14.0;

    /// <summary>
    /// Render a scenario into a PNG, then compare with the baseline.
    /// </summary>
    public static void Verify(string name, int rows, int columns, string ansiSequence)
    {
        var control = new TerminalControl
        {
            FontFamily = new System.Windows.Media.FontFamily(SnapshotFontFamily),
            FontSize = SnapshotFontSize,
        };

        var buffer = new ScreenBuffer(rows, columns);
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(Encoding.UTF8.GetBytes(ansiSequence));
        buffer.ApplyAll(events);
        control.Buffer = buffer;

        control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max((int)Math.Ceiling(control.DesiredSize.Width), 1);
        var height = Math.Max((int)Math.Ceiling(control.DesiredSize.Height), 1);
        control.Arrange(new Rect(0, 0, width, height));

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(control);

        var actualPng = EncodePng(rtb);

        if (IsRegenerating())
        {
            WriteBaselineToSource(name, actualPng);
            return;
        }

        var baselinePath = ResolveBaselinePath(name);
        if (!File.Exists(baselinePath))
        {
            WriteFailureArtifacts(name, actualPng, expected: null, diff: null);
            throw new Xunit.Sdk.XunitException(
                $"Baseline PNG missing for '{name}'. Expected at: {baselinePath}\n" +
                "Re-run with environment variable REGEN_BASELINES=1 to create it.");
        }

        var expectedPng = File.ReadAllBytes(baselinePath);
        var expectedBitmap = DecodePng(expectedPng);

        var (passes, diffPng) = ComparePixels(rtb, expectedBitmap, MaxChannelDiff, MaxDiffPixelRatio);
        if (!passes)
        {
            WriteFailureArtifacts(name, actualPng, expectedPng, diffPng);
            throw new Xunit.Sdk.XunitException(
                $"Snapshot '{name}' does not match baseline (tolerance: " +
                $"{MaxChannelDiff}/255 per channel, {MaxDiffPixelRatio:P0} pixels). " +
                "Actual + diff PNGs written to TestResults/snapshots/. " +
                "If the change is intentional, re-run with REGEN_BASELINES=1.");
        }
    }

    private static bool IsRegenerating()
    {
        var v = Environment.GetEnvironmentVariable("REGEN_BASELINES");
        return !string.IsNullOrEmpty(v) && v != "0" && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBaselinePath(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "baselines", name + ".png");
    }

    private static string ResolveBaselineSourcePath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CopilotSessionManager.Terminal.Wpf.Tests.csproj")))
        {
            dir = dir.Parent;
        }
        if (dir == null)
        {
            throw new InvalidOperationException("Could not locate test project source directory from " + AppContext.BaseDirectory);
        }
        var path = Path.Combine(dir.FullName, "baselines", name + ".png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static void WriteBaselineToSource(string name, byte[] pngBytes)
    {
        var sourcePath = ResolveBaselineSourcePath(name);
        File.WriteAllBytes(sourcePath, pngBytes);

        // Also drop it into the output directory so a subsequent test run
        // in the same session sees the new baseline without a rebuild.
        var outputPath = ResolveBaselinePath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, pngBytes);
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static BitmapSource DecodePng(byte[] pngBytes)
    {
        using var ms = new MemoryStream(pngBytes);
        var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }

    private static (bool Passes, byte[]? DiffPng) ComparePixels(
        BitmapSource actual,
        BitmapSource expected,
        int maxChannelDiff,
        double maxDiffPixelRatio)
    {
        if (actual.PixelWidth != expected.PixelWidth || actual.PixelHeight != expected.PixelHeight)
        {
            // Size mismatch is always a hard failure; emit a diff PNG sized
            // to the actual buffer with everything red.
            var diff = MakeUniformDiff(actual.PixelWidth, actual.PixelHeight);
            return (false, diff);
        }

        var width = actual.PixelWidth;
        var height = actual.PixelHeight;
        var stride = width * 4;
        var actualPixels = new byte[stride * height];
        var expectedPixels = new byte[stride * height];

        var actualBgra = ConvertToBgra32(actual);
        var expectedBgra = ConvertToBgra32(expected);
        actualBgra.CopyPixels(actualPixels, stride, 0);
        expectedBgra.CopyPixels(expectedPixels, stride, 0);

        var diffPixels = new byte[stride * height];
        var totalPixels = width * height;
        var differing = 0;
        for (var i = 0; i < actualPixels.Length; i += 4)
        {
            var db = Math.Abs(actualPixels[i + 0] - expectedPixels[i + 0]);
            var dg = Math.Abs(actualPixels[i + 1] - expectedPixels[i + 1]);
            var dr = Math.Abs(actualPixels[i + 2] - expectedPixels[i + 2]);
            var da = Math.Abs(actualPixels[i + 3] - expectedPixels[i + 3]);

            var maxChannel = Math.Max(Math.Max(db, dg), Math.Max(dr, da));
            if (maxChannel > maxChannelDiff)
            {
                differing++;
                diffPixels[i + 0] = 0;
                diffPixels[i + 1] = 0;
                diffPixels[i + 2] = 255;
                diffPixels[i + 3] = 255;
            }
            else
            {
                diffPixels[i + 0] = actualPixels[i + 0];
                diffPixels[i + 1] = actualPixels[i + 1];
                diffPixels[i + 2] = actualPixels[i + 2];
                diffPixels[i + 3] = 64;
            }
        }

        var ratio = (double)differing / totalPixels;
        if (ratio <= maxDiffPixelRatio)
        {
            return (true, null);
        }

        var diffBitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, diffPixels, stride);
        return (false, EncodePng(diffBitmap));
    }

    private static BitmapSource ConvertToBgra32(BitmapSource source)
    {
        // Always normalize to non-premultiplied Bgra32 so a freshly
        // rendered Pbgra32 RenderTargetBitmap and a PNG-decoded Bgra32
        // baseline compare like-for-like. For fully opaque pixels both
        // formats hold identical bytes; for any transparency the
        // premultiplication would skew the channel diff otherwise.
        if (source.Format == PixelFormats.Bgra32)
        {
            return source;
        }
        return new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
    }

    private static byte[] MakeUniformDiff(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 255;
            pixels[i + 3] = 255;
        }
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        return EncodePng(bmp);
    }

    private static void WriteFailureArtifacts(string name, byte[] actualPng, byte[]? expected, byte[]? diff)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "snapshots");
        Directory.CreateDirectory(dir);

        File.WriteAllBytes(Path.Combine(dir, name + ".actual.png"), actualPng);
        if (expected != null)
        {
            File.WriteAllBytes(Path.Combine(dir, name + ".expected.png"), expected);
        }
        if (diff != null)
        {
            File.WriteAllBytes(Path.Combine(dir, name + ".diff.png"), diff);
        }
    }
}
