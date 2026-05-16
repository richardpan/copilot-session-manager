# VT vocabulary

This document is the supported / ignored / known-gap matrix for the
`CopilotSessionManager.Terminal` parser and `ScreenBuffer`. It is the
authoritative reference for what the embedded terminal (epic
[#93](https://github.com/richardpan/copilot-session-manager/issues/93))
will and will not render. It is derived from real traces captured with
[`tools/CapturePtyTrace`](../tools/CapturePtyTrace) (see also
[`docs/guides/capture-pty-trace.md`](guides/capture-pty-trace.md) for how
to add a new trace).

Phase reference: this matrix tracks the state at the close of Phase 2D
(parser + screen buffer + capture utility + conformance harness). Each
trace catalogued at the bottom of this document is replayed against
`ScreenBuffer` by `tests/CopilotSessionManager.Terminal.Tests/Conformance/`
and snapshot-diffed; any deliberate change to parser or buffer
behavior surfaces as a textual diff to the committed
`*.snapshot.txt` files.

---

## Recognized `VtEvent` types (Phase 2A)

Every VT byte the parser sees is dispatched as one of the following
events. `ScreenBuffer.Apply(VtEvent)` pattern-matches on the union to
mutate state.

| Event | Source sequence(s) | Behavior in `ScreenBuffer` |
|---|---|---|
| `Print(Rune)` | UTF-8 codepoint | Stamps a `TerminalCell` at the cursor with the current `TerminalStyle`; advances cursor with deferred-wrap. |
| `Carriage Return` | `0x0D` | Cursor → column 1. Clears pending-wrap. |
| `LineFeed` | `0x0A` | Cursor row += 1 (raw VT — does **not** carry CR). Scrolls primary buffer into scroll-back at bottom. |
| `Backspace` | `0x08` | Cursor column -= 1 (clamped at column 1). |
| `HorizontalTab` | `0x09` | Cursor → next multiple of 8 columns, clamped at last column. |
| `RingBell` | `0x07` | No-op in buffer. (Renderer concern; will surface as a UI flash in Phase 3.) |
| `CursorMove(direction, n)` | `ESC [ n A/B/C/D` | Up/Down/Right/Left by `n` (default 1), clamped to buffer edges. |
| `CursorPosition(row, col)` | `ESC [ r ; c H` / `... f` | Move cursor to 1-based `(row, col)`, clamped to buffer. |
| `EraseInDisplay(EraseMode)` | `ESC [ n J` | See Erase modes below. Fills with current background (BCE). |
| `EraseInLine(EraseMode)` | `ESC [ n K` | Erases on the cursor's row only. BCE. |
| `Sgr(SgrParameter[])` | `ESC [ ... m` | Updates `TerminalStyle`. See SGR table below. |
| `SaveCursor` | `ESC 7` (DECSC) | Snapshots cursor + style; per-buffer slot. |
| `RestoreCursor` | `ESC 8` (DECRC) | Restores from per-buffer slot; no-op if empty. |
| `SetMode(code, enable)` | `ESC [ ? n h/l` (DECSET) | Recognised modes below; others are no-ops (logged in Phase 3 diagnostics). |
| `EnterAlternateScreen` | `ESC [ ? 1049 h` | Switches `_active` to `_alternate`; no scroll-back. |
| `ExitAlternateScreen` | `ESC [ ? 1049 l` | Switches back to `_primary`. |
| `SetWindowTitle(string)` | `ESC ] 0 ; <title> BEL` (OSC 0/2) | Updates `WindowTitle` property. |
| `ResetTerminal` | `ESC c` (RIS) | Clears both buffers, scroll-back, save slots, style, modes; cursor → (1,1). |
| `UnknownSequence(string)` | anything the parser can't classify | No-op in buffer. Surfaced as a counter on `ScreenBuffer.UnknownSequencesSeen`. |

### Erase modes

| `EraseMode` | Triggering parameter | Meaning |
|---|---|---|
| `ToEnd` | `0` (default) | Cursor (inclusive) → end of region |
| `ToStart` | `1` | Start of region → cursor (inclusive) |
| `Whole` | `2` | Whole region |
| `Scrollback` | `3` (only `EraseInDisplay`) | Drops all scroll-back; viewport untouched |

### Recognized `SetMode` codes

| Code | Name | Effect |
|---|---|---|
| 25 | DECTCEM | Show / hide cursor (renderer concern; tracked on buffer for Phase 3). |
| 1049 | xterm alternate buffer | Same as `EnterAlternateScreen` / `ExitAlternateScreen`. |
| 7 | DECAWM (auto-wrap) | Tracked (`AutoWrap` property); deferred-wrap behavior already aligns with `enable=true` (the default). |

All other DECSET codes (1, 12, 1000-1006, 2004, etc.) are accepted by
the parser as `SetMode` events but are **no-ops** in the buffer. They
will be revisited in Phase 3 if the WPF control needs them (mouse
tracking, bracketed paste, focus events, etc.).

---

## Recognized SGR parameters

| Codes | Effect |
|---|---|
| `0` | Reset all attributes to defaults; clears foreground and background back to `TerminalColor.Default`. |
| `1` | Bold on. |
| `2` | Dim / faint on. |
| `3` | Italic on. |
| `4` | Underline on. |
| `5` | Blink on (renderer may ignore). |
| `7` | Reverse video on (renderer swaps fore/back at draw time). |
| `8` | Conceal on. |
| `9` | Strikethrough on. |
| `21` | Double underline / Bold-off (interpreted as Bold-off here). |
| `22` | Bold + Dim off. |
| `23`–`29` | Italic, Underline, Blink, Reverse, Conceal, Strikethrough off. |
| `30`–`37` | Foreground = palette 0–7. |
| `38;5;n` | Foreground = palette n (0–255). |
| `38;2;r;g;b` | Foreground = 24-bit RGB. |
| `39` | Foreground reset to default. |
| `40`–`47` | Background = palette 0–7. |
| `48;5;n` | Background = palette n (0–255). |
| `48;2;r;g;b` | Background = 24-bit RGB. |
| `49` | Background reset to default. |
| `90`–`97` | Foreground = bright palette 8–15. |
| `100`–`107` | Background = bright palette 8–15. |

Subparameter colon syntax (`38:5:n` / `38:2::r:g:b`) is **not** parsed
in Phase 2A. ConPTY emits the semicolon form, which is what we see in
every captured trace under `samples/traces/`. We will add colon parsing
when (and only when) a captured trace requires it.

---

## Intentionally unsupported (Phase 2)

The following surfaces are recognised by the parser as
`UnknownSequence` events and are explicitly not implemented in the
buffer. Each entry has a deferred-action note.

| Surface | Example | Why deferred |
|---|---|---|
| Mouse tracking | `ESC [ ? 1000 h`–`1006 h` | The Phase 3 WPF control will own mouse handling. The buffer doesn't need a model. |
| Bracketed paste | `ESC [ ? 2004 h`, `ESC [ 200 ~`/`ESC [ 201 ~` | Same — Phase 3 / 4 input pipeline. |
| Focus events | `ESC [ ? 1004 h` | UI concern. ConPTY emits these on init; we observe them as no-ops. |
| Scroll regions (DECSTBM) | `ESC [ t ; b r` | Required for `less`-style pagers. Defer until a captured trace requires it. |
| Character set selection | `ESC ( B`, `ESC ) B`, etc. | We are UTF-8 only. ConPTY tracking confirms it never emits these for `pwsh` / `cmd` / Copilot CLI. |
| Soft reset (DECSTR) | `ESC [ ! p` | Use `ResetTerminal` (RIS) instead; ConPTY appears to use RIS already. |
| Character protection | `ESC V`, `ESC W`, etc. | Vintage VT terminal feature; unused by modern shells. |
| Tab stop set/clear | `ESC H`, `ESC [ g` | Hard-coded to every-8 in the buffer. None of the captured traces tweak this. |
| DECREQTPARM, DECSTBM, DECCRA, DECRARA, etc. | various | Out of scope until proven needed. |

---

## Known gaps (will revisit)

Items below are gaps in the *implementation*, not deliberate
omissions. Tracked here so Phase 3 / Phase 4 don't surprise us.

- **Wide-character / emoji width.** The buffer treats every
  `Rune` as one column. Combining marks and CJK fullwidth characters
  will mis-render. Need to integrate a width-table (e.g. derived from
  Unicode `EastAsianWidth` + `wcwidth` quirks) before the embedded
  terminal renders Copilot CLI output that includes emoji.
- **Reflow on resize.** `ScreenBuffer.Resize` copies the top-left
  rectangle but does not reflow long lines that wrapped at the old
  width. ConPTY does its own reflow on the upstream side, so for
  Copilot CLI output we should be fine in the short term. Will revisit
  if Phase 3 user-resizes show artifacts.
- **Hyperlink OSC 8.** Copilot CLI emits `ESC ] 8 ; ; <url> ESC \\`
  for tracebacks. Currently parsed as `UnknownSequence`. Want a
  `Hyperlink(start/end)` event for Phase 4 click-to-open.
- **Sixel / iTerm2 inline images.** Out of scope; will not implement.

---

## Captured traces

The following traces are committed under `samples/traces/` and replayed
by the Phase 2D conformance harness (`TraceConformanceTests`):

| File | Captured from | What it exercises | Snapshot |
|---|---|---|---|
| `pwsh-color.trace.bin` | `pwsh -Command "Write-Host -Foreground Green/Red/Yellow ..."` | SGR foreground (palette 9/10/11), CR/LF, OSC title set. | `pwsh-color.trace.snapshot.txt` |
| `dir-listing.trace.bin` | `cmd /c dir /a-d C:\Windows\System32\drivers\etc` | Plain-text wrapping, CR/LF, OSC title set, no SGR. | `dir-listing.trace.snapshot.txt` |

To add a new trace:

```pwsh
.\tools\CapturePtyTrace\bin\Release\net8.0-windows\CapturePtyTrace.exe `
    --out samples\traces\my-scenario.trace.bin `
    --cols 100 --rows 30 `
    -- <command line>
```

The first test run after adding a new trace will write the initial
`*.snapshot.txt` and fail with a message telling you to inspect it.
Commit both the `.trace.bin` / `.trace.json` and the snapshot, and the
harness will lock them in.

To regenerate snapshots after a deliberate parser / buffer change:

```pwsh
$env:CSM_REGEN_SNAPSHOTS = "1"
dotnet test tests\CopilotSessionManager.Terminal.Tests --filter "FullyQualifiedName~TraceConformance"
Remove-Item Env:\CSM_REGEN_SNAPSHOTS
git diff samples/traces/
```

See [`docs/guides/capture-pty-trace.md`](guides/capture-pty-trace.md)
for capture usage details and gotchas.
