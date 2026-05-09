# 0001. Use ConPTY for the embedded terminal

- **Status:** Accepted
- **Date:** 2026-05-08
- **Deciders:** @richardpan
- **Related issues:** #30
- **Related ADRs:** —

## Context and problem statement

The app needs to host a terminal experience for each Copilot CLI session inside
its own window. The Copilot CLI is a TUI that uses ANSI escape sequences,
cursor positioning, alternate-screen mode, and reads keystrokes via raw stdin.
Whatever we choose has to faithfully reproduce a real Windows console.

## Decision drivers

- Must render the Copilot CLI's TUI correctly (cursor, colors, alt screen)
- Must let the user type interactively, including arrow keys, Ctrl+C, etc.
- Must be resizable to follow the WPF terminal control's size
- Must be safe to kill (the process should not lose Copilot session state)
- Should not require launching a separate top-level window

## Considered options

1. **Hosted PowerShell runspace** via `System.Management.Automation`
2. **External `pwsh.exe` window** (just `Process.Start` and let Windows host it)
3. **ConPTY (Windows Pseudo Console)** with a custom WPF terminal control

## Decision

We chose **ConPTY**. We will provide an "Open in external terminal" escape
hatch (Option 2) for users who prefer it, but the primary, default experience
is an embedded ConPTY-hosted terminal.

## Consequences

### Positive

- Real TTY behavior — Copilot's TUI, PSReadLine, and any tool that uses ANSI
  escape sequences will work correctly
- Sessions live inside our app window — no taskbar sprawl or alt-tab cost
- Crash safety is essentially free: Copilot persists everything to disk before
  prompting; killing the child process loses no session data (see
  `events.jsonl` durability investigation in #29)
- Standard Windows mechanism — same primitive used by Windows Terminal and
  VS Code's integrated terminal

### Negative

- Implementation complexity: P/Invoke to `CreatePseudoConsole`,
  `ResizePseudoConsole`, `ClosePseudoConsole`, plus an ANSI/VT parser
- Need a WPF terminal control that renders glyphs and handles keyboard input
  (we can build minimal one or wrap an existing library)
- Resize must propagate from WPF control → ConPTY size, with debouncing
- Per-session memory overhead from the parser and scrollback buffer

### Neutral

- Requires Windows 10 1809+ (already a stated minimum for the app)
- The escape hatch (Option 2) needs UI affordances and settings to remember
  the user's preference

## Pros and cons of the options

### Option 1: Hosted PowerShell runspace

- Pro: Pure managed code, no P/Invoke
- Pro: Easy to capture output as objects
- **Con: Not a real TTY.** PSReadLine, Copilot's TUI, and anything that does
  cursor positioning or alt-screen rendering will break or behave bizarrely
- Con: Some Copilot CLI features (TUI prompts, ESC handling) wouldn't work
- Verdict: **Disqualifying** for our use case

### Option 2: External `pwsh.exe` window

- Pro: Trivial implementation (`Process.Start("pwsh.exe", ...)`)
- Pro: Users get the full fidelity of their terminal of choice
- Con: Defeats the embedded-terminal value proposition of the app
- Con: Window management (focus, raising existing windows) is harder via
  unmanaged Win32 calls than just owning the surface ourselves
- Verdict: Good as an escape hatch, not as the default

### Option 3: ConPTY

- Pro: Real TTY, full TUI fidelity
- Pro: Embedded experience matches Windows Terminal / VS Code expectations
- Pro: Owned by us — we can wire focus, status detection, drag-out, etc.
- Con: Implementation effort — VT parser + WPF rendering control
- Con: We need to handle resize, scroll, copy/paste, and keyboard input ourselves
- Verdict: Best fit for the app's vision

## Notes

- Reference implementations: [Windows Terminal](https://github.com/microsoft/terminal)
  uses ConPTY internally; [Conpty Console](https://github.com/microsoft/terminal/tree/main/src/cascadia/TerminalControl) is the WPF/WinUI control
- Possible WPF-friendly libraries to evaluate during implementation:
  - `Microsoft.Terminal.Wpf` (preview/unofficial, varies in maintenance)
  - Homegrown control with a VT parser like
    [`vtparse`](https://github.com/haberman/vtparse) ported to C#
- Decision will be revisited if a maintained turnkey WPF terminal control
  emerges before we ship V1
