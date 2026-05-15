# ADR-0006 — VT100 parser & terminal buffer choice

**Status:** Accepted (2026-05-15).
**Context:** Phase 2 of epic [#93](https://github.com/richardpan/copilot-session-manager/issues/93) (ConPTY embedded terminal). Spike tracked in [#162](https://github.com/richardpan/copilot-session-manager/issues/162). Builds on [ADR-0001](./0001-conpty-for-embedded-terminal.md) (ConPTY for the embedded terminal) and Phase 1 ([PR #161](https://github.com/richardpan/copilot-session-manager/pull/161)).

## Decision

We will **hand-roll a minimal VT100/xterm-subset parser + screen-buffer model** in `CopilotSessionManager.Native` (or a sibling assembly), scoped to the escape-sequence vocabulary that Copilot CLI actually emits. We will not take a third-party dependency for parsing.

## Options considered

ADR-0001 listed three options. Concrete data gathered during the spike:

### Option A — `Microsoft.Terminal.Wpf` (the WPF wrapper around the Windows Terminal control)

| Datapoint | Finding |
|---|---|
| Source available? | Yes — `microsoft/terminal/src/cascadia/WpfTerminalControl/` |
| Target framework | `net472;net8.0-windows` (perfect technical fit) |
| Project version | `0.1` (still alpha) |
| Native dependency | Carries `Microsoft.Terminal.Control.dll` for win-x86 / win-x64 / win-arm64 — built by the Windows Terminal C++/WinRT chain |
| Officially on nuget.org? | **No.** |
| Microsoft's stance | Closed issue [microsoft/terminal#15404](https://github.com/microsoft/terminal/issues/15404) ("The WPF nuget package isn't signed by our CI") indicates Microsoft has explicitly declined to publish a supported package |
| What's on nuget.org | Two unofficial repackages: `CI.Microsoft.Terminal.Wpf` (1.22.250204002, 3.7K downloads, by `CI2NugetRepackageTeam`) and `loloc.Terminal.Wpf` (1.15.2210, 0.5K downloads). Both unsigned. |
| License of source | MIT (Windows Terminal repo) |

**Verdict: rejected.** Microsoft providing the source but explicitly *not* publishing a supported NuGet is a strong signal we shouldn't take a load-bearing dependency on it via an unsigned community repackage. The supply-chain risk and the lack of a Microsoft-supported upgrade path outweigh the technical-fit win.

### Option B — Community VT parser libraries

Searched nuget.org for `VtNetCore`, `XtermSharp`, `vt100.net`, `VT100Parser`, `Pty.Net`, `AnsiParser`. Only two have any usable footprint:

| Package | Status | Notes |
|---|---|---|
| `VtNetCore` 1.0.30 | netstandard2.0, MIT, 12 stable versions | Last release **2021-07-15** (~5 years stale). Single-author hobby project (Darren R. Starr). Functional but unmaintained. |
| `XtermSharp` 1.0.0-alpha.10 | Alphas only | Three pre-release builds, no stable. Mostly used in the Mono / Xamarin terminal ecosystem. |
| `vt100.net`, `VT100Parser`, `AnsiParser` | Not on nuget.org | — |
| `Pty.Net` | Pre-release only (0.1.16-pre) | Not a parser — it's another ConPTY wrapper, overlaps Phase 1 |

**Verdict: rejected.** `VtNetCore` is the best of the bunch but five years unmaintained for a load-bearing UX component is a worse risk profile than writing the focused subset ourselves.

### Option C — Hand-roll a minimal parser scoped to Copilot CLI's VT vocabulary

Copilot CLI emits a bounded subset of the VT100 / xterm vocabulary (we'll catalogue it formally in the implementation issue). For our use case:

- We ship single-file self-contained — every dependency raises build size and audit surface.
- ADR-0002 chose to read Copilot CLI storage directly; we already accept being version-coupled to the CLI. The same coupling makes a focused parser a sound investment.
- Copilot's escape-code surface is dominated by SGR colour, cursor positioning, line / screen erase, cursor visibility, mode-set / -reset, OSC window-title, and a handful of DECSET sequences. That's small.
- We retain crash-safety control: a parser fault is contained inside our process, contained inside a single tab.

**Verdict: accepted.** Effort estimate: comparable to *and not significantly more than* gluing VtNetCore to a custom WPF renderer (most of the work in either case is the renderer + threading, not the parser itself).

## Implementation outline (for the follow-up issue)

1. **Vocabulary catalogue.** Run Copilot CLI through ConPTY + `tee`-style logger for representative sessions; classify every escape sequence observed; produce a "supported / ignored / TODO" matrix.
2. **Parser.** State machine over the byte stream → typed events (`PrintGlyph`, `MoveCursor`, `EraseLine`, `SetSgr`, `SetCursorVisibility`, `SetTitle`, `SetMode`, …). Pure C#, no UI dependency. Heavily unit-tested with table-driven cases.
3. **Screen buffer.** Fixed-size ring of `Cell` rows; each cell carries glyph + SGR attributes + dirty bit. Resize semantics defined explicitly (preserve content top-aligned, clip below). Scroll-back in a separate ring.
4. **Renderer (separate component, Phase 3).** WPF `Control` that observes the screen buffer, batches updates into glyph runs, draws via `DrawingContext`. Threading: parser on a background thread, render marshals to UI thread on dirty-region debounce.
5. **Conformance harness.** Replay recorded byte streams, snapshot the screen buffer, diff against expected output.

## Consequences

**Positive:**
- Zero new third-party dependencies for a load-bearing component.
- Full control over crash-safety, threading, and buffer semantics.
- Aligned with the existing ADR-0002 read-only / version-coupled design philosophy.
- No supply-chain risk from an unsigned community repackage of an unsupported Microsoft binary.

**Negative:**
- More code for us to own and test.
- We are responsible for keeping pace with whatever new escape sequences Copilot CLI starts using; the CLI-adapter pattern (ADR-0003) is the relief valve here.
- A Phase 2 timeline measured in weeks rather than days.

## Open questions deferred to the implementation issue

- **Scrollback persistence** across app restarts (also called out in ADR-0001).
- **Resize debounce parameters** — Copilot CLI re-renders on every resize; we want to debounce.
- **Clipboard formats** — plain text only initially; ANSI / RTF later if there's user demand.
- **Wide-character & emoji handling** — likely deferred until proven necessary.
