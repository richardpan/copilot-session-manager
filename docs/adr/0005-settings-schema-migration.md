# 0005. Settings schema versioning and migration

- **Status:** Accepted
- **Date:** 2026-05-08
- **Deciders:** @richardpan
- **Related issues:** #43
- **Related ADRs:** ADR-0004 (the app DB this protects also needs schema migrations)

## Context and problem statement

Two pieces of state we own will evolve over time:

1. `%LOCALAPPDATA%\CopilotSessionManager\settings.json` — user preferences
2. `%LOCALAPPDATA%\CopilotSessionManager\app.db` — augmentation tables

If we ship without a migration story, version N+1 of the app will either
crash on N's data or silently corrupt it. Bake this in from day one — it is
much harder to add later.

## Decision drivers

- Forward compatibility (older app reading newer files): graceful refusal
- Backward compatibility (newer app reading older files): automatic migration
- Recoverability when a migration fails
- Discoverable for future contributors (a clear pattern to follow)

## Considered options

1. **No versioning** — let it fail, fix in field as needed
2. **Implicit versioning** by file presence/absence of fields
3. **Explicit `schemaVersion` field + migration framework**

## Decision

We chose **Option 3**.

### Settings (`settings.json`)

```jsonc
{
  "schemaVersion": 1,
  "...": "..."
}
```

- On startup, if `schemaVersion < CurrentSettingsVersion`:
  1. Backup current file to `settings.json.bak.<oldVersion>.<timestamp>`
  2. Run registered `ISettingsMigration` instances in version order
  3. Write the migrated settings back
- If `schemaVersion > CurrentSettingsVersion` (older app, newer file):
  - Show banner: "Settings were created by a newer version. Continuing with
    defaults; your file is preserved."
  - Do NOT overwrite the existing file; load defaults in memory only

### App DB (`app.db`)

- A single-row `schema_version` table (`version INTEGER NOT NULL`)
- Migrations registered as ordered `IDbMigration` instances
- Each migration runs inside a transaction
- On failure: rollback transaction, restore from `app.db.bak.<oldVersion>`,
  surface error

### Migration framework expectations

- Migrations are **forward-only** (no down-migrations) — simpler and matches
  our threat model
- Migrations must be **idempotent** where possible
- A "Reset settings" / "Reset app DB" pair of advanced-settings actions for
  unrecoverable cases

## Consequences

### Positive

- Clear contract for future contributors
- Recoverable in normal failure modes via backups
- Rollout-safe: shipping a new version with a migration is routine
- Older builds can read newer files in degraded mode without crashing

### Negative

- Slight up-front cost: define the framework and discipline
- Backups accumulate over time — need a cleanup policy
  (keep last 5 per type, prune older)
- "Forward-only" means a downgrade path requires manual surgery

### Neutral

- Some users may end up with unbounded `.bak.*` files if their machines
  upgrade frequently — handled by the cleanup policy

## Pros and cons of the options

### Option 1: No versioning

- Pro: Less code now
- Con: Painful incidents on every breaking change
- Con: Bug reports look like "the app deleted my settings"

### Option 2: Implicit versioning

- Pro: No explicit field
- Con: Ambiguous when fields are merely absent because the user didn't set them
- Con: Becomes a tangle of `if field in dict` checks
- Verdict: Unmaintainable past version 2

### Option 3: Explicit + migration framework

- Pro: Predictable, testable, recoverable
- Con: Requires discipline (worth it)

## Notes

- Migrators are ordered by integer version, registered via DI in the
  composition root
- Each migrator has a unit test that takes a fixture of the prior schema and
  asserts the post-state
- The current schema versions live in `Constants.SettingsSchemaVersion` and
  `Constants.DbSchemaVersion` (one place to bump)
- Backup retention: keep the last 5 backups per file; prune older on startup
