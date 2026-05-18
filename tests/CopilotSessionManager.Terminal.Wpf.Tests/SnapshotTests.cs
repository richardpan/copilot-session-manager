namespace CopilotSessionManager.Terminal.Wpf.Tests;

/// <summary>
/// Issue #181: PNG baseline tests for the TerminalControl renderer. Each
/// test renders a tiny scenario (8x4 cells) through the real
/// VtParser/ScreenBuffer/TerminalControl pipeline and compares against a
/// checked-in baseline under <c>baselines/</c>. The harness is tolerant
/// of small font-hinting variations across hosts; see
/// <see cref="SnapshotHarness"/> for details.
///
/// To refresh baselines after an intentional rendering change:
///   <c>$env:REGEN_BASELINES = "1"; dotnet test --filter SnapshotTests</c>
/// </summary>
public class SnapshotTests
{
    [Fact]
    public void Plain_text_baseline() => StaRunner.Run(() =>
    {
        SnapshotHarness.Verify("plain_text", rows: 4, columns: 16, ansiSequence: "Hello, world!");
    });

    [Fact]
    public void Palette_8color_baseline() => StaRunner.Run(() =>
    {
        // Standard 8-colour ANSI sweep on row 1; reset before next row.
        var seq = "\u001B[31mR\u001B[32mG\u001B[33mY\u001B[34mB\u001B[35mM\u001B[36mC\u001B[37mW\u001B[0m";
        SnapshotHarness.Verify("palette_8color", rows: 4, columns: 8, ansiSequence: seq);
    });

    [Fact]
    public void Palette_256color_baseline() => StaRunner.Run(() =>
    {
        // 256-color foreground: pick a handful of distinctive entries.
        var seq =
            "\u001B[38;5;196mA" +
            "\u001B[38;5;46mB" +
            "\u001B[38;5;21mC" +
            "\u001B[38;5;226mD" +
            "\u001B[38;5;201mE" +
            "\u001B[38;5;51mF" +
            "\u001B[38;5;208mG" +
            "\u001B[38;5;15mH" +
            "\u001B[0m";
        SnapshotHarness.Verify("palette_256color", rows: 4, columns: 8, ansiSequence: seq);
    });

    [Fact]
    public void Truecolor_rgb_baseline() => StaRunner.Run(() =>
    {
        var seq =
            "\u001B[38;2;255;0;0mR" +
            "\u001B[38;2;0;255;0mG" +
            "\u001B[38;2;0;0;255mB" +
            "\u001B[38;2;255;255;0mY" +
            "\u001B[38;2;255;0;255mM" +
            "\u001B[38;2;0;255;255mC" +
            "\u001B[38;2;128;128;128mX" +
            "\u001B[38;2;255;255;255mW" +
            "\u001B[0m";
        SnapshotHarness.Verify("truecolor_rgb", rows: 4, columns: 8, ansiSequence: seq);
    });

    [Fact]
    public void Background_color_baseline() => StaRunner.Run(() =>
    {
        // Mixed foreground/background: alternating cells.
        var seq =
            "\u001B[41;37m R " +
            "\u001B[42;30m G " +
            "\u001B[0m";
        SnapshotHarness.Verify("background_color", rows: 4, columns: 8, ansiSequence: seq);
    });

    [Fact]
    public void Reverse_video_baseline() => StaRunner.Run(() =>
    {
        // CSI 7 m swaps fg/bg. Text "ABCD" then reverse on "EFGH".
        var seq = "ABCD\u001B[7mEFGH\u001B[0m";
        SnapshotHarness.Verify("reverse_video", rows: 4, columns: 8, ansiSequence: seq);
    });
}
