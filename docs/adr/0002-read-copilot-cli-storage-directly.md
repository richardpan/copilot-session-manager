# 0002. Read Copilot CLI's session storage directly

- **Status:** Accepted
- **Date:** 2026-05-08
- **Deciders:** @richardpan
- **Related issues:** #29, #31, #35
- **Related ADRs:** ADR-0003 (handles the version-drift risk this introduces)

## Context and problem statement

Originally we planned to maintain our own SQLite store mirroring everything
about each Copilot session — IDs, names, repository, branch, token totals,
status history. Investigation (#29) revealed that the Copilot CLI already
maintains all of this in `~/.copilot/`:

- `session-store.db` — global SQLite with `sessions`, `turns`, `checkpoints`,
  `session_files`, `session_refs`, FTS5 `search_index_*`, `schema_version`
- `session-state/<uuid>/workspace.yaml` — per-session metadata
- `session-state/<uuid>/events.jsonl` — full event stream
- `session-state/<uuid>/inuse.<PID>.lock` — active session indicator

If we maintain a parallel store, it will drift, double the work to keep in
sync, and obscure what Copilot actually believes.

## Decision drivers

- Truth — the dashboard should show what Copilot CLI thinks, not our cached
  copy
- Less code is better
- Auto-improve when Copilot adds new fields without us touching anything
- Avoid corrupting Copilot's data

## Considered options

1. **Maintain our own full mirror** of session metadata and sync on a timer
2. **Read Copilot's storage directly** (read-only) and keep only an
   app-specific augmentation table
3. **Hybrid**: read-through cache with TTL, falling back to Copilot's storage

## Decision

We chose **Option 2**. We read Copilot's storage directly via:

- `Microsoft.Data.Sqlite` opened read-only with WAL-aware busy timeout
- `FileSystemWatcher` on `~/.copilot/session-state/` and `session-store.db`

Our **app-only SQLite** stores only what Copilot doesn't track:

- Session type label (V1 feature, #2)
- Custom group / folder assignment
- Pinned status
- README customization markers (versioning info)
- App settings (separate file actually, but same DB context)

## Consequences

### Positive

- Significantly less code to write and maintain
- Cannot drift — there's only one source of truth for Copilot data
- New session metadata fields surface automatically
- Crash safety inherited for free — Copilot persists, we just render
- Lower disk footprint and reduced risk of write conflicts

### Negative

- We're coupled to Copilot CLI's on-disk layout and event format. **Mitigated
  by ADR-0003.**
- We must respect the SQLite WAL writer (open `Mode=ReadOnly`, set
  `PRAGMA busy_timeout`)
- Any crash in Copilot leaves us with whatever was flushed; we can't paper
  over partial state with our own cache

### Neutral

- All access goes through `ICopilotSessionRepository` so a future "hybrid
  cache" mode could be added without touching consumers

## Pros and cons of the options

### Option 1: Full mirror

- Pro: Fast queries against our schema
- Pro: We control the schema and migration cadence
- **Con: Drift is inevitable** when Copilot writes faster than we sync
- Con: Doubles the disk footprint
- Con: Implementing FTS, joins, etc. that Copilot already provides

### Option 2: Read directly + app augmentation table

- Pro: Truth, less code, free improvements
- Con: Coupled to Copilot's format (handled by ADR-0003)

### Option 3: Hybrid TTL cache

- Pro: Could be faster on slow machines
- Con: Combines both downsides — code complexity and drift potential
- Con: Premature optimization until we measure read latency

## Notes

- Read patterns we expect to need (drove the decision):
  - List all sessions with last-modified, repo, branch (`session-store.db`)
  - Resolve a session id → `workspace.yaml` for `name`, `user_named`
  - Tail `events.jsonl` for live status / token tracking
  - Detect lock files for active state
- We will benchmark cold-start enumeration against ~100 sessions before V1
  and revisit if we hit scaling issues
