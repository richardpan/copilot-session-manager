# 0004. Encrypt app DB at rest with DPAPI

- **Status:** Accepted
- **Date:** 2026-05-08
- **Deciders:** @richardpan
- **Related issues:** #42
- **Related ADRs:** ADR-0002 (defines what's in the app DB)

## Context and problem statement

The app DB stores user-customized augmentation: session type labels, custom
group assignments, pinned status, README versioning markers, app settings.
None of this is highly sensitive on its own, but it correlates user activity
with paths, repositories, and Copilot session IDs. Industry hygiene calls for
local data to be encrypted at rest.

## Decision drivers

- Local-only data; no cross-machine sync
- Zero user friction (no extra password prompts)
- Single-user, single-machine threat model
- Avoid third-party crypto dependencies if we can

## Considered options

1. **Plaintext SQLite** — rely on filesystem ACLs alone
2. **SQLCipher** with DPAPI-wrapped key
3. **Plaintext SQLite + selective DPAPI for sensitive columns**

## Decision

We chose **Option 2**. The app DB is encrypted with **SQLCipher**, and the
SQLCipher key is wrapped using **Windows DPAPI**
(`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`).

- Key file: `%LOCALAPPDATA%\CopilotSessionManager\app-db.key`
  (DPAPI-encrypted blob)
- DB file: `%LOCALAPPDATA%\CopilotSessionManager\app.db`
  (SQLCipher-encrypted)

On first run:

1. Generate a random 32-byte key
2. DPAPI-encrypt under `CurrentUser` scope
3. Write to `app-db.key` (ACL'd to current user only)
4. Open the DB with `PRAGMA key`

On subsequent runs:

1. Read `app-db.key`
2. DPAPI-decrypt
3. Open the DB with `PRAGMA key`

## Consequences

### Positive

- DB is unreadable if copied to another user's profile or machine
- Fully transparent — DPAPI does not prompt
- Defends against casual snooping, backup misconfiguration, and lost-device
  scenarios
- Sets a good precedent for any sensitive fields we add later

### Negative

- Adds **SQLCipher** dependency (native binaries shipped)
- Slight performance overhead per read/write (negligible for our workload)
- DB cannot be read by tools like DB Browser for SQLite without the key
  (a debug helper may be useful — see Notes)

### Neutral

- Out of scope: encrypting Copilot CLI's own files (not our domain — they live
  under the user's profile already)
- Out of scope: cross-user sharing
- Out of scope: hardware-backed key storage (TPM) — could revisit for a
  future enterprise mode

## Pros and cons of the options

### Option 1: Plaintext

- Pro: Simplest
- Pro: Zero deps
- Con: Anything that can read the user profile can read the DB
- Con: Backups, sync, error reports may all leak the file

### Option 2: SQLCipher + DPAPI

- Pro: All data encrypted at rest
- Pro: Transparent UX
- Con: Native dependency

### Option 3: Plaintext + selective column encryption

- Pro: No SQLCipher dependency
- Con: Easy to forget to encrypt a new column
- Con: More code, more error surface
- Con: Schemas with mixed clear/encrypted columns can leak via metadata
  (column statistics, indexes)

## Notes

- DPAPI scope is `CurrentUser` (not `LocalMachine`) — copies between user
  profiles will fail to decrypt, which is the desired behavior
- We will provide a `--reset-app-db` CLI flag to handle key loss
  (e.g., user profile reset)
- Optional debug helper: a "Show app DB key" action gated behind a setting,
  for developer use; redacted from logs
- Re-keying (rotation) is out of scope for V1 but supported via SQLCipher's
  `PRAGMA rekey` if needed later
