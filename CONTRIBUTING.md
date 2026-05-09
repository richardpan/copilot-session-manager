# Contributing to Copilot Session Manager

Thanks for your interest! This document describes the local dev setup, branch and commit conventions, and the PR checklist.

## Prerequisites

- **Windows 10 1809+ or Windows 11** (the app is Windows-only by design)
- **.NET 8 SDK** (or any newer SDK; the app targets `net8.0-windows`)
- **PowerShell 7+** (`pwsh.exe`) on PATH
- **GitHub CLI** (`gh`) authenticated to your GitHub account
- **GitHub Copilot CLI** (`copilot`) installed
- **git** 2.40+

Optional but recommended:

- **Visual Studio 2022 17.8+** with the *.NET desktop development* workload
- **Rider 2024.1+**
- **VS Code** with the C# Dev Kit extension

## Getting the code

```powershell
git clone https://github.com/richardpan/copilot-session-manager.git
cd copilot-session-manager
dotnet restore
dotnet build
```

## Project layout

See [`README.md`](README.md#project-structure) for the canonical layout. In short:

- `src/CopilotSessionManager`       — WPF app (UI + composition root)
- `src/CopilotSessionManager.Core`  — pure logic, file IO, SQLite adapters
- `src/CopilotSessionManager.Native` — ConPTY / Win32 P/Invoke
- `tests/`                          — xUnit unit & integration tests
- `docs/`                           — long-form docs
- `docs/adr/`                       — Architectural Decision Records
- `mockup/`                         — UI mockups
- `.github/`                        — workflows, issue & PR templates

## Branching

- `main` is always shippable
- Feature branches: `feature/<issue-number>-short-slug`  (e.g. `feature/32-lock-detection`)
- Fix branches:     `fix/<issue-number>-short-slug`
- Chore branches:   `chore/<short-slug>`
- Docs-only:        `docs/<short-slug>`

## Commit messages

Follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <short summary>

<optional body>

Refs: #<issue>
```

Types: `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `build`, `ci`, `perf`, `style`.
Scope is the affected area (`core`, `ui`, `native`, `infra`, etc.).

Examples:

```
feat(core): add inuse.lock-based active session detection
fix(ui): debounce token bar updates to 1Hz
docs(adr): add ADR-001 ConPTY decision
```

## Code style

- C# 12, nullable reference types **enabled**, implicit usings **enabled**
- `dotnet format` is enforced in CI; run it before pushing:
  ```powershell
  dotnet format
  ```
- Async methods end in `Async`
- Public APIs need XML doc comments
- Use `var` for obvious-from-RHS, explicit type otherwise
- One public type per file, file name = type name

## Tests

- Unit tests live next to the project they test: `tests/CopilotSessionManager.Core.Tests/`
- Use **xUnit + FluentAssertions + Moq**
- Pure logic = required tests
- File/SQLite touching code = integration tests (slower lane, separate test class)
- Aim for ≥80% coverage on `Core`, ≥60% on Native

Run tests:

```powershell
dotnet test
```

Code coverage:

```powershell
dotnet test --collect:"XPlat Code Coverage"
```

## Pull request checklist

Before opening a PR:

- [ ] `dotnet build` succeeds with no warnings (treat warnings as errors locally)
- [ ] `dotnet format --verify-no-changes` is clean
- [ ] `dotnet test` passes
- [ ] Coverage hasn't dropped more than 2%
- [ ] PR is linked to an issue (`Refs: #N` or `Closes #N`)
- [ ] Architectural changes include or update an ADR under `docs/adr/`
- [ ] If user-visible: screenshot/screencast attached
- [ ] If touching `~/.copilot/` parsing: a fixture test added under `tests/fixtures/`

## Filing issues

- Bugs: use the **Bug report** template
- Feature requests: use the **Feature request** template
- For security issues, see [SECURITY.md](SECURITY.md) — do NOT open a public issue

## Code of Conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating, you agree to abide by its terms.

## License

By contributing, you agree your contributions will be licensed under the [MIT License](LICENSE).
