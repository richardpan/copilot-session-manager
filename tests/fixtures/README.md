# Test fixtures

This directory holds **captured, sanitised** sample data used by integration
tests in `tests/CopilotSessionManager.Core.Tests`. The goal is to exercise
parsing and discovery code against the real on-disk shapes that the Copilot
CLI produces, without depending on a live `~/.copilot/` install.

Tracking issue: [#49 — Test strategy and minimum coverage targets][issue-49].

## What lives here

Planned (see [#49][issue-49] for status):

- `events/` — sample `events.jsonl` files captured from real sessions,
  covering at minimum:
  - a short successful session,
  - a session that hit the token cap,
  - a session that crashed mid-turn,
  - a multi-model session.
- `workspaces/` — sample `workspace.yaml` files including:
  - minimal valid workspace,
  - workspace with sub-projects,
  - workspace with malformed YAML (for negative tests),
  - workspace with non-ASCII paths.
- `sessions/` — directory layouts that mimic `~/.copilot/sessions/<id>/`
  end-to-end (events.jsonl + workspace.yaml + lock file states).

## Rules for new fixtures

1. **No PII.** Strip user names, repo paths, GitHub tokens, machine
   names, IP addresses and email addresses before committing. Replace
   with stable placeholders such as `USER`, `C:\workspaces\demo`,
   `user@example.invalid`.
2. **No GitHub tokens or other secrets**, even revoked ones.
3. **Deterministic.** Avoid embedded timestamps that would force the
   tests to be time-relative; use fixed `2025-01-01T00:00:00Z`-style
   values.
4. **Small.** Trim long sessions to the smallest excerpt that still
   reproduces the case under test.
5. **Documented.** Each new file should have a one-line comment (or a
   sibling `*.md`) describing what scenario it covers.
6. **Referenced by a test.** Fixtures with no test referencing them
   will be removed.

## Loading fixtures from tests

The test csproj copies `fixtures/**/*` to the output directory:

```xml
<ItemGroup>
  <None Update="fixtures\**\*.*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

Resolve a fixture path from a test:

```csharp
var fixturePath = Path.Combine(
    AppContext.BaseDirectory,
    "fixtures",
    "events",
    "short-session.jsonl");
```

## Contributing fixtures

If you sanitise and add a fixture, please update this README's "What lives
here" section and link the test that consumes it.

[issue-49]: https://github.com/richardpan/copilot-session-manager/issues/49
