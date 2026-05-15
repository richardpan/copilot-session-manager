# `CapturePtyTrace` — capturing real ConPTY traces for the conformance harness

`tools/CapturePtyTrace` is a tiny console utility that wraps
`PseudoConsole` (Phase 1 of epic
[#93](https://github.com/richardpan/copilot-session-manager/issues/93))
and writes every byte the child emits to a binary trace file plus a
JSON sidecar with metadata. The conformance harness in Phase 2D will
replay these traces against `ScreenBuffer` and snapshot-diff the
result, so the more representative traces we have, the more confident
we can be in the embedded-terminal foundation.

## Build

```pwsh
dotnet build .\CopilotSessionManager.sln -c Release
```

The tool ends up at:

```
tools\CapturePtyTrace\bin\Release\net8.0-windows\CapturePtyTrace.exe
```

## Usage

```
CapturePtyTrace [options] -- <command line...>

options:
  --out <path>       output trace file (default: trace-<ts>.bin in cwd)
  --metadata <path>  JSON sidecar (default: <out>.json)
  --cols <n>         ConPTY columns (default: 120)
  --rows <n>         ConPTY rows    (default: 30)
  --cwd <path>       working directory for the child
  --mirror           also write captured bytes to this process's stdout
```

Everything before the literal `--` is option parsing; everything after
is reassembled (joined with single spaces) into the command line passed
to `PseudoConsole.Start` — i.e. straight to `CreateProcess`. cmd /c
parsing rules apply for `cmd` invocations.

## Examples

A short colorful pwsh capture (this is the trace already committed
under `samples/traces/pwsh-color.trace.bin`):

```pwsh
$cmd = 'pwsh -NoLogo -NoProfile -Command "$PSStyle.OutputRendering=''Ansi''; ' +
       'Write-Host -ForegroundColor Green green-line; ' +
       'Write-Host -ForegroundColor Red red-line; ' +
       'Start-Sleep -Milliseconds 300"'
.\tools\CapturePtyTrace\bin\Release\net8.0-windows\CapturePtyTrace.exe `
    --out samples\traces\pwsh-color.trace.bin `
    --cols 80 --rows 24 `
    -- cmd /c $cmd
```

A plain-text directory listing:

```pwsh
.\tools\CapturePtyTrace\bin\Release\net8.0-windows\CapturePtyTrace.exe `
    --out samples\traces\dir-listing.trace.bin `
    --cols 100 --rows 30 `
    -- cmd.exe /c dir /a-d "C:\Windows\System32\drivers\etc"
```

A live Copilot CLI session (one-shot prompt, no agent loop):

```pwsh
.\tools\CapturePtyTrace\bin\Release\net8.0-windows\CapturePtyTrace.exe `
    --out samples\traces\copilot-help.trace.bin --mirror `
    -- copilot --help
```

## Output

Two files are produced per capture:

- `<name>.bin` — raw bytes from the child, in the exact order ConPTY
  emitted them. Contains init / cleanup ESC sequences from ConPTY
  itself, plus everything the child wrote.
- `<name>.json` — sidecar with `commandLine`, `columns`, `rows`,
  `capturedAtUtc`, `durationMs`, and `bytesCaptured`. The
  `schema` field is `csm.capture-pty-trace.v1`.

The conformance harness in Phase 2D consumes both files: it uses the
geometry from the sidecar to construct a `ScreenBuffer` of the right
size before feeding the bytes through `VtParser`.

## Gotchas

- **ConPTY collapses fast children.** When a child writes a few bytes
  and exits in well under a second, ConPTY may collapse intermediate
  screen state and emit only the final delta. If you need to capture a
  short interaction reliably, hold the child open for ~200 ms after the
  last write (e.g. `Start-Sleep -Milliseconds 300`).
- **PowerShell expansion.** Variables like `$PSStyle` get expanded by
  *your* outer shell unless you single-quote the inner command (or
  here-string it). Any `$` inside the captured command must survive
  PowerShell tokenization.
- **`cmd.exe /c` quoting.** `cmd.exe /c` parses the rest of the
  command line verbatim. The capture tool joins the post-`--` args
  with single spaces, so you can pass `cmd.exe /c echo a & b` directly
  — but if you want the `&` to belong to cmd (and not to your outer
  PowerShell session), wrap the whole thing in single quotes.
- **Mirror is best-effort.** `--mirror` writes captured bytes to the
  tool's own stdout for live observability. Inside an actual ConPTY
  session those bytes will render as ANSI. In a non-color console they
  will look noisy. Don't rely on `--mirror` to validate what was
  written — read the `.bin` instead.

## Adding a captured trace to the repo

1. Capture under `samples/traces/`.
2. Add a row to the table at the bottom of
   [`docs/vt-vocabulary.md`](../vt-vocabulary.md) describing what the
   trace exercises.
3. The Phase 2D conformance harness will pick it up automatically by
   directory enumeration.
