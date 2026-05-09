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

- All read access to Copilot's storage is funneled through a small set of
  read-only interfaces (see [How we comply](#how-we-comply)), so a future
  "hybrid cache" mode could be slotted in without touching consumers

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

## How we comply

The "Copilot session repository" envisioned by issue #31 is implemented as a
small set of focused read-only interfaces in `CopilotSessionManager.Core`,
each owning one slice of `~/.copilot/`. The combination is what consumers
treat as `ICopilotSessionRepository`; we deliberately split it rather than
build a single wide interface so that test doubles stay tiny and watcher
plumbing only lives where it's needed.

| Concern | Interface | Implementation | Reads from |
|---|---|---|---|
| Resolve `~/.copilot/` paths | `Sessions/ICopilotPaths` | `Sessions/DefaultCopilotPaths` (delegates to `Configuration/AppPaths`) | `%USERPROFILE%\.copilot\` |
| `sessions` / `turns` rows | `Sessions/ISessionStore` | `Sessions/SessionStore` (Microsoft.Data.Sqlite, `Mode=ReadOnly`, `PRAGMA busy_timeout = 5000`) | `~/.copilot/session-store.db` |
| Per-session folder + checkpoints | `Sessions/ISessionFolderReader` | `Sessions/SessionFolderReader` | `~/.copilot/session-state/<id>/` |
| `workspace.yaml` parsing | _internal_ `Cli/Adapters/V1/WorkspaceYamlReader` (via `ICopilotCliAdapter`) | `Cli/Adapters/V1/CopilotCliV1Adapter` | `~/.copilot/session-state/<id>/workspace.yaml` |
| `events.jsonl` streaming | _internal_ `Cli/Adapters/V1/EventsJsonlReader` (via `ICopilotCliAdapter`) | `Cli/Adapters/V1/CopilotCliV1Adapter` | `~/.copilot/session-state/<id>/events.jsonl` |
| Active-session lock files | `Sessions/ISessionLockMonitor` | `Sessions/SessionLockMonitor` | `~/.copilot/session-state/<id>/inuse.<PID>.lock` |
| Combined view + change notifications | `Sessions/ISessionDiscoveryService` | `Sessions/SessionDiscoveryService` (composes all of the above + `FileSystemWatcher`) | all of `~/.copilot/` |

`Configuration/AppPaths` is the single source of truth for both sides and
documents the read-only invariant in its XML doc comment:

> All app-owned data lives under `%LOCALAPPDATA%\CopilotSessionManager\`.
> We never write inside `~/.copilot/`; that folder is treated as read-only
> Copilot CLI state.

App-only writes are limited to:

| What | Where | Why it's app-only |
|---|---|---|
| Session-type labels (#2) | `%LOCALAPPDATA%\CopilotSessionManager\labels.json` via `Sessions/JsonSessionLabelStore` | Copilot CLI has no concept of label/category |
| Settings | `%LOCALAPPDATA%\CopilotSessionManager\settings.json` via `Settings/JsonAppSettingsStore` | UI/user prefs |
| Reserved app DB | `%LOCALAPPDATA%\CopilotSessionManager\app.db` (`AppPaths.AppDatabasePath`) — encrypted per ADR-0004 | Future app-only augmentation |
| Logs | `%LOCALAPPDATA%\CopilotSessionManager\logs\` via Serilog | Diagnostics |

`SESSION-README.md` is written **inside the session's working directory**
(not under `~/.copilot/`) by `Sessions/FileSessionReadmeStore` so it
travels with the repo and can be committed.

## Join keys

There is exactly one join key between Copilot's storage and our app-only
storage: **the Copilot session id** — the lowercase GUID-shaped string the
CLI assigns when a session is created (e.g.
`9f3b1c2d-7a4e-4d8e-9f10-abcdef012345`).

It originates in Copilot's `sessions.id` column (`session-store.db`) and
flows through our code as `string Id` everywhere it appears:

- `Sessions/SessionStoreRecord.Id` — value read from `sessions.id`
- `Models/Session.Id` — propagated unchanged into the unified view
- `Sessions/ICopilotPaths.SessionStateDirectory` + `<Id>` resolves the
  per-session folder on disk
- `Sessions/ISessionLabelStore` keys (`labels.json`'s `labels` map)
- `Sessions/FileSessionReadmeStore` lookups via `ISessionFolderReader`
- All future rows in the encrypted `app.db` MUST use a `session_id TEXT`
  column whose value is the same string

Rules for the join key:

1. **Treat as opaque.** Do not parse, validate as a GUID, or transform case.
   Compare with `StringComparison.OrdinalIgnoreCase` (the labels store
   already does).
2. **Never invent one.** App-only rows are only created in response to a
   session id we observed in `~/.copilot/`.
3. **Don't cascade-delete.** When Copilot's session disappears, our app-only
   rows for that id stay until the user (or a future cleanup job) removes
   them. We never react to Copilot's deletes by writing — that would violate
   the "read-only" half of this ADR.

## Notes

- Read patterns we expect to need (drove the decision):
  - List all sessions with last-modified, repo, branch (`session-store.db`)
  - Resolve a session id → `workspace.yaml` for `name`, `user_named`
  - Tail `events.jsonl` for live status / token tracking
  - Detect lock files for active state
- We will benchmark cold-start enumeration against ~100 sessions before V1
  and revisit if we hit scaling issues
- Issue #31 originally proposed a single `ICopilotSessionRepository`
  interface. We chose the split-by-concern shape above instead so each
  collaborator can be mocked in isolation and so the per-folder vs.
  global-DB readers don't get coupled.
