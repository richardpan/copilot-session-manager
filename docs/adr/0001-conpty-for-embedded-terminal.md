# 0001. Use ConPTY for the embedded terminal

- **Status:** Proposed
- **Date:** 2026-05-09
- **Deciders:** @richardpan
- **Related issues:** #30, #29, #1, #38
- **Related ADRs:** ADR-0002 (read Copilot CLI's session storage directly), ADR-0003 (versioned CLI adapter)

## Context and problem statement

Copilot Session Manager is a WPF dashboard for managing GitHub Copilot CLI
sessions. Today, opening a session means spawning a separate top-level
PowerShell window via `ISessionLauncher` —
`src/CopilotSessionManager.Core/Sessions/PowerShellSessionLauncher.cs`
shells out to `pwsh.exe -NoExit -Command "copilot --resume '<id>'"` through
`IProcessLauncher`/`ShellFileLauncher` and trusts Windows to host the
console. This works (the CLI is fully interactive in that window), but the
experience is awkward:

- **Taskbar sprawl** — each open session is a separate window competing for
  attention; we already track them with hand-rolled
  `CopilotSessionManager.Native` window enumeration code so we can re-focus
  an existing window when the user re-clicks a session card. That code is
  fragile: it depends on window-title heuristics, OS version, and the user
  not having renamed their PowerShell prompt.
- **Focus stealing** — `Process.Start(UseShellExecute: true)` brings the new
  window to the foreground regardless of whether the user wanted to context
  switch.
- **No window↔session association in the UI** — the dashboard can know a
  session is "open" only by polling `inuse.<PID>.lock` (see ADR-0002) and
  matching it back to a window handle. Status indicators, contextual
  actions, and the planned share/merge wizard (#38) cannot live alongside
  the terminal because the terminal isn't ours.
- **No embedded scrollback or in-app keybinding integration** — features
  like "jump to last error", per-session SESSION-README pinning (#1), or
  inline display of the current Copilot status need a terminal surface the
  app actually owns.

The Copilot CLI itself is a TUI. It uses ANSI escape sequences, cursor
positioning, alternate-screen mode, and reads keystrokes via raw stdin.
Whatever surface we host has to faithfully reproduce a real Windows console
or the CLI will misbehave (PSReadLine glitches, broken prompts, garbled
output).

## Decision drivers

- Must render the Copilot CLI's TUI correctly (cursor, colors, alt-screen)
- Must let the user type interactively, including arrow keys, Ctrl+C, Tab
  completion, and any sequence the CLI cares about
- Must be resizable to follow the WPF terminal control's size
- Must be safe to kill — the embedded process should not lose Copilot
  session state if the WPF host crashes (Copilot persists everything on
  disk per the durability investigation in #29)
- Should not require a separate top-level OS window
- Should leave room for "open in your real terminal" as an opt-out

## Considered options

1. **Hosted PowerShell runspace** via `System.Management.Automation`
2. **External `pwsh.exe` window only** — keep today's behavior as the
   default, just polish the launcher
3. **Browser-based xterm.js inside WebView2**
4. **Existing third-party WPF terminal control** (e.g. `WPFTerm`,
   `terminal.gui` adapter)
5. **ConPTY (Windows Pseudo Console)** with a custom WPF terminal control,
   plus a "Detach to external window" escape hatch that falls back to today's
   `PowerShellSessionLauncher`

## Decision

We chose **Option 5: ConPTY** as the default in-app terminal experience.
The existing `PowerShellSessionLauncher` stays in the codebase and is
re-exposed as a "Detach to external window" command, so users who prefer
their own terminal lose nothing.

## Rationale

- **Real TTY semantics.** ConPTY is the same primitive used by Windows
  Terminal and VS Code's integrated terminal. Anything Copilot's TUI does —
  alternate-screen mode, cursor positioning, color, raw input — works
  out of the box because we are a real pseudo-console client.
- **Battle-tested.** Microsoft maintains the API and the open-source
  consumers; we're inheriting a well-trodden integration path rather than
  inventing one.
- **Owns the surface.** Once the terminal lives in our window, every UI
  affordance the dashboard wants — per-session status badges, share/merge
  wizard (#38), session README pane (#1), keybinding integration — has a
  natural home next to the terminal instead of in a sibling top-level
  window.
- **Crash-safe by construction.** Copilot's session state is durable on
  disk (verified in #29 and consumed read-only per ADR-0002), so a crash
  of the WPF terminal control loses no Copilot context. The user can
  reopen the session and pick up exactly where they left off.

## Consequences

### Positive

- Real TTY behavior — Copilot's TUI renders faithfully
- Sessions live inside our window; no taskbar sprawl, no focus stealing
- Status, share, merge, and SESSION-README UI can sit next to the terminal
- Window-find native code becomes optional rather than load-bearing
- Standard Windows mechanism — same primitive used by Windows Terminal

### Negative

- Implementation complexity: P/Invoke to `CreatePseudoConsole`,
  `ResizePseudoConsole`, `ClosePseudoConsole`, `STARTUPINFOEX`, and
  `UpdateProcThreadAttribute`, plus an ANSI/VT parser and a custom WPF
  rendering control
- Resize must propagate from WPF size changes → ConPTY size with debouncing
  to avoid flicker on drag
- Per-session memory overhead from the parser and scrollback buffer
- Threading model must keep the parser off the UI thread while still
  marshaling renders back onto it

### Neutral

- Requires Windows 10 1809+, already a stated minimum for the app
- The escape hatch needs a UI affordance and a setting to remember the
  user's preference

## Implementation notes

This ADR ships the *decision*; the implementation is deferred to a tracking
epic and broken into the phases below. None of these phases land in this
PR; this section exists so future implementers know the agreed shape.

### Phase 1: P/Invoke layer in `CopilotSessionManager.Native`

Add the `CreatePseudoConsole`, `ResizePseudoConsole`, `ClosePseudoConsole`,
`STARTUPINFOEX`, `InitializeProcThreadAttributeList`, and
`UpdateProcThreadAttribute` declarations alongside the existing
`NativeMarker` placeholder. Own the canonical handle/pipe lifecycle
(four anonymous pipe handles, `HPCON`, attribute list memory) in a single
`PseudoConsole` `IDisposable`. Reference: Microsoft's
[pseudoconsole sample](https://github.com/microsoft/terminal/tree/main/samples/ConPTY).

### Phase 2: VT100 parser + terminal buffer

Three options, in rough order of preference:

- **Option A — `Microsoft.Terminal.Wpf`** if it is currently shipping a
  net8.0-windows-compatible build. Smallest code surface, official-ish
  source. Risk: package has historically lagged behind the Windows
  Terminal repo and may not target net8.0-windows.
- **Option B — A community parser** such as a port of `vtparse`. Adds a
  dependency we don't fully control. License must be compatible.
- **Option C — Hand-roll a minimal parser** covering the subset of VT
  sequences Copilot's TUI emits. Highest control, highest maintenance
  burden — every Copilot CLI release becomes a parser-coverage check.

Pick at the start of Phase 2 after a one-day spike against a real Copilot
session capture. Prefer A → B → C.

### Phase 3: WPF terminal control

An embedded `Control` that subscribes to parser events, renders the
buffer (likely a `DrawingVisual` or `Canvas` with glyph runs), handles
resize via `OnRenderSizeChanged`, mouse selection, and forwards keyboard
input down the input pipe. Threading: parser runs on a background thread;
renders are dispatched to the UI thread via `Dispatcher.BeginInvoke`;
keyboard input flows from the UI thread back through the input pipe.

### Phase 4: Integration with session cards

Replace the per-card "Open in PowerShell" button with "Open terminal",
which hosts the new control inside an expanded card area or a docked
terminal pane (UX TBD). The pane needs a header with session name, a
"Detach to external window" button (Phase 5), and a close affordance.
Open question: do we persist scrollback between app restarts, or is it
in-memory only?

### Phase 5: "Detach to external window" command

Keep `PowerShellSessionLauncher` exactly as it is today. The detach
command tears down the embedded ConPTY for that session (the Copilot
process exits cleanly when its stdin/stdout pipes close — the durable
state is already on disk per #29) and then invokes the existing launcher
with the same `sessionId`. The user's running task picks up in the
external pwsh window.

## Acceptance criteria

These are copied verbatim from issue #30. They are **deferred** to the
follow-up implementation epic; this ADR does not satisfy them on its own.

- [ ] ConPTY-based terminal control renders Copilot's TUI correctly
- [ ] Resize works
- [ ] Copy/paste works
- [ ] Scrollback buffer
- [ ] "Detach to external window" action available

## Pros and cons of the options

### Option 1: Hosted PowerShell runspace

- Pro: Pure managed code, no P/Invoke
- Pro: Easy to capture output as objects
- **Con: Not a real TTY.** PSReadLine, alt-screen rendering, and the
  Copilot TUI break or behave bizarrely
- Con: No SIGWINCH equivalent, no Ctrl+C, no resize signal
- Verdict: **Disqualifying** for our use case

### Option 2: External `pwsh.exe` window only

- Pro: Trivial — already implemented in `PowerShellSessionLauncher`
- Pro: Users get the full fidelity of their terminal of choice
- Con: Defeats the embedded-terminal value proposition
- Con: Window management (refocus, enumerate by title) is fragile and
  OS-version-dependent — the existing native code is already a tax we'd
  rather not double down on
- Verdict: Good as the **escape hatch**, not as the default

### Option 3: WebView2 + xterm.js

- Pro: xterm.js is mature; rendering quality is good
- Con: Adds a WebView2 dependency to a small WPF app
- Con: stdio piping crosses a JS↔native boundary, complicating both
  performance and security review
- Con: Bundle size and startup-cost regression
- Verdict: Rejected — the native ConPTY path is sufficient for our needs

### Option 4: Third-party WPF terminal control

- Pro: Could collapse Phases 2 and 3 into "take a dependency"
- Con: Most candidates are unmaintained, target older .NET, or have
  license terms that don't fit (e.g. GPL on a proprietary ship)
- Con: We still need the ConPTY plumbing to feed the control
- Verdict: Re-evaluate during Phase 2 if a maintained net8.0-windows
  control surfaces; otherwise build on top of `Microsoft.Terminal.Wpf`
  or a hand-rolled parser

### Option 5: ConPTY + custom WPF control + escape hatch

- Pro: Real TTY, full TUI fidelity, owned by us
- Pro: Embedded experience matches Windows Terminal / VS Code expectations
- Pro: Escape hatch removes the regression risk for power users
- Con: Implementation effort spread across the five phases above
- Verdict: **Selected**

## Risks and open questions

- **Scrollback lifetime.** In-memory only, or persisted between restarts?
  Persistence is appealing but requires a serialization format and disk
  budget per session.
- **Resize flicker.** Active Copilot renders during a window drag could
  produce visible artifacts. Debouncing the `ResizePseudoConsole` call is
  a known mitigation; needs to be tested with real workloads.
- **Clipboard formats.** Copy as plain text vs. ANSI vs. RTF; paste
  bracketing; multi-line paste safety.
- **Concurrent-terminal scaling.** How many embedded terminals can we
  host before UI-thread starvation? Need a benchmark before committing
  to "every open card hosts a terminal".
- **Parser maintenance.** A hand-rolled parser is a recurring tax every
  time Copilot CLI starts emitting a new sequence.
- **High-contrast and a11y theming.** The terminal control needs to honor
  the high-contrast palette work tracked in #45 — this couples Phase 3
  to that effort.

## Migration / rollout

The current `PowerShellSessionLauncher` stays in place and remains the
default through Phases 1–3. Only after Phase 4 lands does the dashboard
flip to "embedded terminal" as the default action; Phase 5 ensures the
external launcher is reachable from a one-click command so no user is
worse off than today. If any phase slips, the app still ships V1 with
the working external-window experience.

## References

- [Creating a pseudoconsole session (Microsoft Docs)](https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session)
- [Windows Terminal repository (microsoft/terminal)](https://github.com/microsoft/terminal) — reference consumer of ConPTY and home of `Microsoft.Terminal.Wpf`
- Issue #29 — durability of Copilot CLI session state under abrupt
  process termination (verified)
- ADR-0002 — read Copilot CLI's session storage directly (motivates the
  crash-safety claim above)
