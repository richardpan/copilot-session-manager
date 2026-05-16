# ADR-0007 — WPF terminal rendering strategy

**Status:** Accepted (2026-05-15).
**Context:** Phase 3 of epic [#93](https://github.com/richardpan/copilot-session-manager/issues/93) (ConPTY embedded terminal). Builds on [ADR-0001](./0001-conpty-for-embedded-terminal.md) (ConPTY), [ADR-0006](./0006-vt-parser-choice.md) (hand-rolled VT parser), and Phase 2 ([#164](https://github.com/richardpan/copilot-session-manager/issues/164), shipped through PRs #165–#168).

Phases 1 and 2 produced everything *below* the renderer: a ConPTY host (`CopilotSessionManager.Native`), a VT100 parser (`VtParser`), a typed event stream (`VtEvent`), a screen-buffer model with dirty-row tracking (`ScreenBuffer`), and a deterministic conformance harness that replays captured byte streams against committed snapshots. Phase 3 puts a WPF `Control` on top of that pipeline.

## Decision

We will implement the embedded terminal renderer as a **WPF `Control` that hosts one `DrawingVisual` per terminal row, rendered into pre-cached `GlyphRun`s using a monospace `GlyphTypeface`**. The control will live in a new `CopilotSessionManager.Terminal.Wpf` assembly (`net8.0-windows`, `UseWPF=true`); the existing `CopilotSessionManager.Terminal` assembly stays pure `net8.0` so the parser/buffer remain trivially testable and host-agnostic.

We will **not** take a third-party rendering dependency (SharpDX / SkiaSharp / WriteableBitmapEx). The renderer is implemented against `System.Windows.Media` only.

## Options considered

### Option A — `DrawingVisual` host + per-row `GlyphRun`s

| Datapoint | Finding |
|---|---|
| API surface | `System.Windows.Media.DrawingVisual` + `GlyphRun` + `GlyphTypeface.GetGlyphIndices` |
| Retained mode? | Yes — WPF caches the visual tree; only invalidated rows re-emit drawing instructions |
| Background-thread rendering? | `DrawingVisual` and its `DrawingContext` are UI-thread; parser/buffer can stay on a background thread but `Render` calls marshal back |
| Text quality | Best-in-class: WPF's glyph cache, sub-pixel positioning, ClearType all "just work" |
| Per-cell SGR styling | Natural — split each row into runs by `(fg, bg, attrs)` |
| Dirty-region cost | We already track per-row dirty bits in `ScreenBuffer`; one visual per row maps 1-to-1 |
| Resize cost | Re-measure glyph metrics, re-build the visual list. Done at human pace (resize is rare) |

### Option B — `WriteableBitmap` (CPU-rasterised pixel buffer)

| Datapoint | Finding |
|---|---|
| API surface | `WriteableBitmap.WritePixels` + a software rasteriser we'd write ourselves |
| Text quality | Whatever our software glyph rasteriser delivers — almost certainly worse than WPF's GDI/DirectWrite-backed text |
| Background-thread rendering? | Yes — we can build pixels on a worker, only the final `WritePixels` is UI-thread |
| Per-cell SGR styling | We'd write the colour blender ourselves |
| Dirty-region cost | Cheap (only rewrite dirty cell rectangles) but we'd have to maintain a glyph atlas |
| Re-use of platform features | None — bypasses WPF's text stack entirely |

### Option C — `SharpDX` / Direct2D / SwapChain

| Datapoint | Finding |
|---|---|
| API surface | `SharpDX.Direct2D1` + `SharpDX.DirectWrite` + `D3DImage` interop |
| Status of SharpDX | `SharpDX` 4.2.0 (2019), repo archived 2020. No first-party successor in the .NET ecosystem |
| Text quality | Excellent (DirectWrite directly) |
| Operational complexity | Adds DirectX device-lost handling, swap-chain plumbing, native-interop debugging, single-file packaging concerns |
| Single-file publish? | Possible but adds extra audit surface |
| Deployment | Native dependency for every supported architecture |

### Option D — `FormattedText` only (no visual tree)

| Datapoint | Finding |
|---|---|
| API surface | `DrawingContext.DrawText(FormattedText)` from a single `OnRender` |
| Retained mode? | No — every change repaints everything inside `OnRender`. Equivalent to immediate mode |
| Performance | Adequate for the buffer sizes we care about but no dirty-row payoff |
| Threading | All in UI thread |

## Decision drivers

- **We already do per-row dirty tracking** (`ScreenBuffer.IsRowDirty(...)`). Option A maps 1-to-1 onto that — the renderer becomes "for each dirty row, rebuild its visual; everything else is retained."
- **No new third-party dependencies.** Same posture as ADR-0006. The renderer is the second load-bearing terminal component; we want both end-to-end on platform primitives.
- **Single-file publish stays simple.** No native DLLs to pack.
- **Text quality is non-negotiable** for a developer-facing terminal. Option A reuses WPF's text stack (ClearType, sub-pixel positioning, hinting) for free; Option B forces us to write it.
- **Background-thread parsing.** The parser already runs cleanly off-thread (`ApplyAll` is single-threaded but the parser→buffer pipeline can sit on a dedicated reader task). The rendering hot path under Option A is "Dispatcher.BeginInvoke a small payload per dirty row," which is appropriate for the bandwidth ConPTY produces.
- **Testability.** A pure `IScreenBufferSnapshot → DrawingContext` projection function can be exercised by a `RenderTargetBitmap` + pixel hash in an xUnit `STAThread` test runner. We already do something analogous in the conformance harness.

## Pros and cons of the options

### Option A — DrawingVisual + GlyphRun

- ✅ Native fit for our existing dirty-row model.
- ✅ Best text quality with zero extra work.
- ✅ Zero third-party dependencies.
- ✅ Retained-mode caching makes scroll-only frames nearly free.
- ⚠️ All `DrawingVisual` mutation is UI-thread. We marshal per dirty-row payloads via the dispatcher; not a real problem at ConPTY data rates but worth measuring.
- ⚠️ `GlyphRun` requires per-character glyph indices — one indirection through `GlyphTypeface.CharacterToGlyphMap` (we cache it).

### Option B — WriteableBitmap + software rasteriser

- ✅ Threading is the simplest possible — almost everything moves off the UI thread.
- ✅ Dirty-region updates are pixel-exact and cheap.
- ❌ Text quality degrades unless we re-implement a meaningful subset of DirectWrite.
- ❌ We'd own a glyph atlas, font-metric cache, colour-blending fast paths, and DPI handling.
- ❌ Significantly more code than Option A for no observable user-facing win.

### Option C — SharpDX / Direct2D

- ✅ Highest theoretical performance ceiling.
- ❌ Archived dependency (SharpDX). No first-party replacement in .NET. We would either pin a 2019 library or write our own DirectWrite interop.
- ❌ Adds device-lost handling, swap-chain plumbing, and native-interop debugging.
- ❌ At the buffer sizes a terminal needs, the performance ceiling is invisible relative to Option A.

### Option D — `FormattedText`-only immediate mode

- ✅ Trivial to implement.
- ❌ Throws away our per-row dirty tracking — every frame repaints the whole viewport.
- ❌ `FormattedText` is heavier than `GlyphRun` per call and was never designed for tight redraw loops.

## Consequences

### Positive

- Renderer maps directly onto the dirty-row signal Phase 2B already exposes.
- No new package references, no native DLLs, no single-file packaging churn.
- Text quality matches the rest of the WPF UI without effort.
- The decomposition `CopilotSessionManager.Terminal` (portable) ⇄ `CopilotSessionManager.Terminal.Wpf` (UI) keeps the parser/buffer trivially testable and lets us add other hosts later if we ever want one.

### Negative

- The renderer pays the cost of marshalling per-dirty-row payloads onto the UI thread. We assume this is fine at ConPTY rates and will validate by load-test before locking Phase 3 down (see "Performance budget" below).
- Bidirectional-text and complex shaping are not within reach of a hand-rolled `GlyphRun` setup. Monospace, left-to-right, BMP-only is the supported contract. Wide-character (East Asian) and emoji handling are explicitly out of scope for Phase 3.

### Neutral

- The control will own its own font and font size settings. Settings-system integration will happen in a later phase; for Phase 3 they default to Cascadia Mono → Consolas → generic monospace fallback.

## Implementation outline (for the follow-up issue)

1. **New project `CopilotSessionManager.Terminal.Wpf`** (`net8.0-windows`, `UseWPF=true`).
   - References `CopilotSessionManager.Terminal`.
   - `InternalsVisibleTo` its test project.
2. **Cell metrics.** A `CellMetrics` value-type computed from `(Typeface, FontSize, Dpi)` — cell width, cell height, baseline, glyph index cache.
3. **`TerminalControl : Control`.**
   - DPs: `Buffer` (ScreenBuffer), `FontFamily`, `FontSize`, `Foreground`, `Background`.
   - Hosts a `DrawingVisualHost` (a `FrameworkElement` exposing `VisualChildrenCount` / `GetVisualChild`).
   - One `DrawingVisual` per row + one for the cursor.
4. **`ScreenBuffer` dirty-row signal.** Add an event (`event EventHandler<ViewportInvalidatedEventArgs>`) that the renderer subscribes to. Coalesce on the dispatcher.
5. **Render pass.** For each dirty row, split into style-runs, build a `GlyphRun`, draw the run and any background rectangles. Reset the row's dirty bit only after the render commits.
6. **Cursor.** Separate `DrawingVisual` that repaints on a 500 ms blink timer; visible iff `ScreenBuffer.CursorVisible`.
7. **Resize.** On `SizeChanged`, recompute viewport dimensions in cells, call `ScreenBuffer.Resize(rows, cols)`, re-emit a full repaint.
8. **Threading.**
   - Parser sits on a dedicated reader task; mutates `ScreenBuffer` directly.
   - `ScreenBuffer` raises `ViewportInvalidated` on whichever thread mutated it.
   - The control's subscription dispatches to UI thread with `Dispatcher.BeginInvoke(DispatcherPriority.Render, ...)`.
9. **Input (Phase 3C).** `OnKeyDown` / `OnTextInput` / `OnMouseDown` translate to byte sequences and call into the ConPTY input stream. Bracketed-paste aware.
10. **Selection & clipboard (Phase 3D).** Cell-rectangle selection model; copy as plain text; respect `ScreenBuffer.BracketedPasteEnabled` on paste.
11. **Wiring (Phase 3E).** A small `TerminalSession` façade composes ConPTY + parser + buffer + control. MainWindow gets a debug menu item to launch one over `pwsh` for end-to-end validation.

The above maps to sub-PRs **3A → 3E** under a new "Phase 3 implementation" umbrella issue (sibling to #164). 3A is the scaffolding PR and ships an empty-but-bound control; subsequent PRs land each capability with its own tests.

## Performance budget

We will validate Option A against the following targets before merging Phase 3. If any of these fail by more than 20%, we re-open this ADR.

| Scenario | Budget |
|---|---|
| Full-viewport repaint (80×24, no scroll-back) | < 8 ms on a typical dev box |
| `pwsh` cold prompt (Phase 2 trace replay) | < 50 ms wall-clock end-to-end |
| 1 MB / s sustained ConPTY output | < 30% UI-thread occupancy, no visible jank during scroll |
| `dotnet test --no-build` floor | No regression in Terminal-tests wall-clock |

The conformance harness already provides repeatable byte streams; we can re-use its trace files as the load-test corpus.

## Open questions deferred to the implementation issue

- **Scrollback UI.** ADR-0001 left scroll-back persistence open; this ADR also defers the UX (mouse-wheel through ring buffer, search inside scroll-back) to Phase 4.
- **Font configuration.** Picking the font in the Settings dialog is post-Phase-3 work.
- **Selection model details** — line vs. block, double-click word selection, etc. — locked in inside Phase 3D.
- **High-DPI re-snap on monitor change.** The renderer reads DPI from `PresentationSource`; we'll add a `DpiChanged` handler if Phase 3E reveals it matters.
- **Accessibility.** A `TerminalControlAutomationPeer` is desirable but out of scope until a screen-reader user reports a need — tracked separately so Phase 3 isn't blocked on it.

## Notes

- The split into a portable `CopilotSessionManager.Terminal` and a WPF-specific `CopilotSessionManager.Terminal.Wpf` is the same shape Windows Terminal uses (`TerminalCore` vs. `Microsoft.Terminal.Control`) and roughly what Avalonia.Terminal does in the cross-platform world. We're not inventing a new factoring.
- Should we ever want a non-WPF host (a CLI dump, a CI screenshot tool, a hypothetical Avalonia front-end), the existing core stays untouched.
- The conformance harness validates the `parser → buffer` boundary; Phase 3 will introduce a parallel harness for the `buffer → bitmap` boundary using `RenderTargetBitmap` + pixel hashes against committed PNGs. Snapshots live next to the existing trace snapshots under `samples/traces/`.
