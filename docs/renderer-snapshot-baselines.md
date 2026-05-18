# Renderer snapshot baselines

Issue #181 follows up ADR-0007 ([0007-wpf-terminal-rendering.md](adr/0007-wpf-terminal-rendering.md))
with PNG-baseline regression tests for `TerminalControl`. The baselines
live next to the WPF test project:

```
tests/CopilotSessionManager.Terminal.Wpf.Tests/baselines/*.png
```

## What is covered

`SnapshotTests` renders a deterministic ScreenBuffer scenario through the
real `VtParser` → `ScreenBuffer` → `TerminalControl` pipeline and
compares the rendered `RenderTargetBitmap` against a checked-in PNG.
Current scenarios:

| Baseline | Scenario |
| --- | --- |
| `plain_text` | Default fg/bg ASCII text |
| `palette_8color` | 8-color SGR sweep (`CSI 31..37 m`) |
| `palette_256color` | 256-color SGR (`CSI 38;5;N m`) |
| `truecolor_rgb` | 24-bit SGR (`CSI 38;2;R;G;B m`) |
| `background_color` | Combined fg + bg (`CSI 4N;3N m`) |
| `reverse_video` | `CSI 7 m` inverse run |

## Comparison tolerance

Pixel comparison is tolerant by design:

- per-channel diff up to `SnapshotHarness.MaxChannelDiff` (16/255) is ignored;
- up to `SnapshotHarness.MaxDiffPixelRatio` (1 %) of pixels may exceed
  that threshold before the check fails.

This forgives small font-hinting differences between dev machines and CI
without losing the ability to flag real visual regressions (palette
shifts, wrong glyphs, missing background rects).

## Updating baselines

After an intentional rendering change, regenerate the PNGs and commit
the new files:

```pwsh
$env:REGEN_BASELINES = "1"
dotnet test tests/CopilotSessionManager.Terminal.Wpf.Tests/CopilotSessionManager.Terminal.Wpf.Tests.csproj `
    -c Release --filter "FullyQualifiedName~SnapshotTests"
Remove-Item Env:REGEN_BASELINES

git add tests/CopilotSessionManager.Terminal.Wpf.Tests/baselines/*.png
```

The harness writes back to the source `baselines/` directory and to the
build output, so you can immediately re-run without `REGEN_BASELINES`
to confirm the new files pass.

## Failures

When a snapshot fails the harness drops three PNGs into
`TestResults/snapshots/` under the test project's build output:

- `<name>.actual.png` — what the renderer produced;
- `<name>.expected.png` — the committed baseline;
- `<name>.diff.png` — bright-red pixels where the channel diff exceeded
  the threshold, dim-alpha otherwise.

Open the diff PNG to see which cells changed, then decide whether to fix
the rendering regression or regenerate the baseline.

## Fonts

The harness asks for `Cascadia Mono, Consolas, Courier New` so WPF can
fall back through that chain when the preferred face is missing. Cascadia
Mono ships with Windows 10 1903+ and Windows Server 2022 — including the
`windows-2022` GitHub Actions runner used in CI — so baselines stay
portable across the supported hosts.
