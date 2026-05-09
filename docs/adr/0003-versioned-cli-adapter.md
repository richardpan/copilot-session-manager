# 0003. Versioned adapter layer for Copilot CLI compatibility

- **Status:** Accepted
- **Date:** 2026-05-08
- **Deciders:** @richardpan
- **Related issues:** #37
- **Related ADRs:** ADR-0002 (this ADR mitigates a risk introduced by ADR-0002)

## Context and problem statement

ADR-0002 commits us to reading Copilot CLI's on-disk state directly. The
Copilot CLI is evolving rapidly; the schema of `session-store.db`, the field
shape of `events.jsonl`, and the structure of `workspace.yaml` will change.
Hard-coded assumptions will break us silently or noisily as users update
their Copilot CLI.

## Decision drivers

- Resilience to upstream changes
- Clear, explicit support boundaries
- Graceful degradation when we encounter an unsupported version
- Testability (we should be able to pin fixtures per version)

## Considered options

1. **Single hard-coded parser** — fix as bugs are reported
2. **Versioned adapter layer** — one implementation per supported CLI major
   version, selected at runtime
3. **Schema-introspection** — parse generically using `JsonElement` /
   `IDictionary<string, object>`, never type fields

## Decision

We chose **Option 2**. The architecture:

```
┌────────────────────────────────────────────┐
│ ICopilotCliAdapter                         │
│   - SupportedRange { MinVersion, MaxVersion}│
│   - ParseSession(id) -> Session            │
│   - ParseEvents(stream) -> IAsyncEnumerable│
│   - ParseWorkspace(yaml) -> Workspace      │
└────────────────────────────────────────────┘
       ▲                  ▲                  ▲
       │                  │                  │
 CopilotCliV1Adapter  CopilotCliV2Adapter  ...
```

- On each session load, read `copilotVersion` from the first
  `session.start` event in `events.jsonl`
- Resolve the right adapter via a registry that knows version ranges
- If no adapter matches → use the most recent adapter and surface a banner:
  "Copilot CLI vX is newer than this app supports. Some fields may be missing
  or wrong. Update the app or report an issue."
- If the file format is wholly unparseable → mark the session "Cannot read"
  and continue with the rest of the dashboard

## Consequences

### Positive

- Explicit, testable contract for what versions we support
- Adding support for a new CLI version is a contained change (new adapter
  class + fixture-based tests, no edits to consumers)
- Old adapters can be retired in their own PR with a clear deprecation window
- Users on too-new versions get a friendly message instead of a crash

### Negative

- Up-front complexity; we'd like a single parser if Copilot's format were
  stable
- Risk of N parsers diverging in subtle behavior — mitigated by a shared test
  suite that runs against every adapter

### Neutral

- The adapter interface itself will evolve. Breaking changes to it are
  internal-only and don't require a new ADR.

## Pros and cons of the options

### Option 1: Hard-coded parser

- Pro: Simplest possible code today
- Con: Every CLI change is a fire drill
- Con: No clean way to support multiple versions at once
- Verdict: Fragile

### Option 2: Versioned adapter layer

- Pro: Resilient, testable, explicit support window
- Con: Higher up-front complexity
- Verdict: Best fit

### Option 3: Schema introspection

- Pro: Theoretically future-proof
- Con: Loses type safety; bugs become silent (missing field reads as null)
- Con: Hard to reason about behavior
- Verdict: Worst of both worlds

## Notes

- Initial supported version: **Copilot CLI 1.0.43**
  (the version against which #29 was investigated)
- Supported window: aim for the most-recent two minor versions, plus best-effort
  for older
- Adapter resolution falls back to "most recent supported" if version parsing
  fails entirely
- Adapters live under `src/CopilotSessionManager.Core/Cli/Adapters/`; fixtures
  under `tests/fixtures/copilot-cli/<version>/`
