# Copilot Session Manager

> A native Windows desktop application for managing your GitHub Copilot CLI sessions — track tokens, organize work, document automatically, and never lose context again.

[![Platform](https://img.shields.io/badge/platform-Windows-0078D4?logo=windows)](https://github.com/richardpan/copilot-session-manager)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Status](https://img.shields.io/badge/status-V1%20in%20development-yellow)]()
[![License](https://img.shields.io/badge/license-MIT-green)]()

---

## Table of Contents

- [Overview](#overview)
- [Why This Exists](#why-this-exists)
- [Key Features](#key-features)
  - [V1 Features](#v1-features)
  - [Future Roadmap](#future-roadmap)
- [How It Works](#how-it-works)
  - [Architecture](#architecture)
  - [Session Lifecycle](#session-lifecycle)
  - [PowerShell Integration](#powershell-integration)
  - [Auto-Generated READMEs](#auto-generated-readmes)
- [Tech Stack](#tech-stack)
- [Screenshots / Mockup](#screenshots--mockup)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Project Structure](#project-structure)
- [Roadmap & Issues](#roadmap--issues)
- [Contributing](#contributing)
- [License](#license)

---

## Overview

**Copilot Session Manager** is a Windows desktop application that gives you a single pane of glass over every GitHub Copilot CLI session running on your machine. Instead of juggling terminal windows, losing track of which session is doing what, and burning through token budgets unnoticed, you get an organized dashboard with live status, cost awareness, automatic documentation, and one-click access to any session's terminal.

Think of it as a **task manager built specifically for AI coding assistants**.

---

## Why This Exists

Modern engineering work with Copilot CLI tends to spawn many parallel sessions:

- One session refactoring auth
- Another building UI components
- A third investigating a production bug
- A fourth writing documentation

Within an hour you have **5+ terminal windows open**, each with hundreds of thousands of tokens of context, and zero way to:

- Tell at a glance which sessions are *actively working* vs *waiting on you* vs *idle*
- See how much budget each session has consumed
- Find the right window when you need to jump back into a specific piece of work
- Remember what each session was actually doing two days later
- Move useful context from one session into a related one without copy-pasting transcripts

Copilot Session Manager solves all of these problems with a native Windows app that hosts and orchestrates the sessions for you.

---

## Key Features

### V1 Features

These four pillars ship in the initial release:

#### 📄 Auto-Generated README Files
Every session gets a living `SESSION-README.md` automatically scaffolded on creation and kept in sync as work progresses. Copilot itself summarizes:

- **Goal** — derived from your initial prompt
- **Status** — current state of the work
- **Files Modified** — running list of what's been touched
- **Key Decisions** — important choices made during the session
- **Next Steps** — what's queued or blocked

Manually edited sections are preserved across regenerations using comment delimiters. READMEs are stored under each session's working directory and version-controlled when possible — making them perfect handoff and audit artifacts.

#### 🏷️ Session Type Labels
Every session gets categorized at creation time so you can filter, group, and report by intent:

| Label | Use Case |
|-------|----------|
| `exploratory` | Open-ended investigation, no fixed outcome |
| `research` | Reading code / docs to learn |
| `feature` | Building something new |
| `bug` | Reproducing and fixing a defect |
| `refactor` | Restructuring existing code |
| `docs` | Writing or updating documentation |
| `infra` | DevOps, CI/CD, deployment work |
| `experiment` | Throwaway prototypes |

Each label has a distinct color and icon. The dashboard supports multi-label filtering and per-type stats.

#### 🤖 Model Indicator + Switcher
See which Copilot model each session is using at a glance, and swap models mid-session without losing context.

- Color-coded badge per session (premium / standard / fast tier)
- One-click model switcher with confirmation when escalating to a more expensive tier
- Cost estimate updates immediately based on the new model's pricing
- Model switches are logged in the session timeline so you can audit "why did this session cost so much?"

#### 🔗 Branch / PR / Issue Links
Sessions don't exist in a vacuum — they relate to git branches, pull requests, and tracked issues. The app surfaces all of these directly on each session card:

- **Branch:** auto-detected from the session's working directory; click to open on GitHub
- **Pull Request:** if the branch has an open PR, the badge shows status (draft / open / merged) and CI check status (passing / failing)
- **Issues:** manually link issues by `#NN` syntax in the README or via the issue picker
- All metadata syncs with GitHub via the authenticated `gh` CLI

---

### Future Roadmap

The V1 release is intentionally focused. Beyond V1, the roadmap (tracked as [GitHub issues](https://github.com/richardpan/copilot-session-manager/issues)) includes:

- ⌨️ **Global keyboard shortcuts** & `Ctrl+K` command palette
- 📌 **Pinned / favorite sessions**
- 📡 **Recent activity feed**
- 📋 **Session templates** (bug-fix, feature, code-review, research, refactor)
- 💰 **Token budget alerts** with auto-checkpoint and seamless restart
- 💵 **Per-session cost estimates (USD)** and trend charts
- 🎯 **Partial context merge** — pick which files / turns / artifacts to copy
- 🔍 **Session diff viewer** for comparing two sessions side-by-side
- 🤝 **Auto-suggested related sessions** based on shared signals
- 🌳 **Sub-sessions / threading** for tangent exploration
- 💤 **Auto-archive idle sessions** with smart restoration
- 📦 **Bulk actions** and **export bundles** (zip with README + transcript + diffs)
- 📜 **README diff history** and **cross-session wiki-links** (`[[Session Name]]`)
- 🔔 **Desktop notifications** for long-running tasks and waiting sessions
- 📁 **Custom groups, saved filters,** and **repo-based auto-grouping**

See the full backlog: [`v1` issues](https://github.com/richardpan/copilot-session-manager/labels/v1) · [`v2` issues](https://github.com/richardpan/copilot-session-manager/labels/v2)

---

## How It Works

### Architecture

The app is a single .NET 8 WPF process that runs as a tray-resident desktop application on Windows:

```
┌─────────────────────────────────────────────────────────────┐
│                  Copilot Session Manager                    │
│                       (WPF / .NET 8)                        │
│                                                             │
│  ┌──────────────┐  ┌───────────────┐  ┌─────────────────┐   │
│  │  Dashboard   │  │  Session      │  │  README Editor  │   │
│  │  View (XAML) │  │  Detail View  │  │  & Markdown     │   │
│  └──────┬───────┘  └───────┬───────┘  │  Renderer       │   │
│         │                  │          └────────┬────────┘   │
│         └──────────────────┼───────────────────┘            │
│                            │                                │
│  ┌─────────────────────────▼───────────────────────────┐    │
│  │              ViewModels (MVVM)                      │    │
│  └─────────────────────────┬───────────────────────────┘    │
│                            │                                │
│  ┌─────────────────────────▼───────────────────────────┐    │
│  │                 Service Layer                       │    │
│  │  • SessionStore   • TokenTracker   • ReadmeService  │    │
│  │  • ProcessHost    • GitHubClient   • ModelRegistry  │    │
│  └─────────────────────────┬───────────────────────────┘    │
│                            │                                │
│  ┌─────────────────────────▼───────────────────────────┐    │
│  │            Platform Integration Layer               │    │
│  │  • System.Management.Automation (PowerShell host)   │    │
│  │  • Win32 P/Invoke (window focus, HWND tracking)     │    │
│  │  • System.IO.FileSystemWatcher (README sync)        │    │
│  │  • LibGit2Sharp (branch / PR detection)             │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
              │                              │
              ▼                              ▼
   ┌─────────────────────┐      ┌──────────────────────────┐
   │  Hosted PowerShell  │      │  GitHub REST + GraphQL   │
   │  Runspace per       │      │  (via gh CLI auth token) │
   │  session            │      │                          │
   └─────────────────────┘      └──────────────────────────┘
```

**Key design choices:**

- **Hosted PowerShell runspaces** — instead of spawning standalone `powershell.exe` windows for every session, the app hosts PowerShell runspaces *in-process* via `System.Management.Automation`. This gives us full control over input/output streams, lifecycle, and lets us render a custom terminal experience while still being a real PowerShell.
- **Optional standalone window mode** — for users who prefer a real PowerShell window, the app can spawn `pwsh.exe`, capture the HWND, and use Win32 `SetForegroundWindow` / `ShowWindow` to focus it on demand.
- **MVVM with CommunityToolkit.Mvvm** — clean separation between UI and logic, making the codebase testable and maintainable.
- **Local SQLite database** — session metadata, token history, and settings persist locally in `%LOCALAPPDATA%\CopilotSessionManager\sessions.db`.

### Session Lifecycle

```
[New]
  │  user creates session (template + label + working dir)
  ▼
[Initializing]
  │  scaffold SESSION-README.md, create runspace, attach watchers
  ▼
[Working] ◄───────────────┐
  │  Copilot is processing │
  ▼                        │
[Awaiting Input]           │
  │  user response needed  │  user replies, work continues
  └───────────────────────►┘
  │
  │  user idle > N hours
  ▼
[Inactive]
  │  optionally archived
  ▼
[Archived]
  │  README + transcript preserved; can be restored
```

Each transition triggers:
- README status header update
- Stats bar refresh
- Optional desktop notification (V2)

### PowerShell Integration

There are two modes:

1. **Hosted mode (default)** — PowerShell runs inside the app. The terminal is rendered as a custom WPF control. Pros: full control, consistent UX, no extra windows cluttering the taskbar. Cons: missing some advanced PSReadLine features.

2. **External window mode** — `pwsh.exe` is launched as a child process. The app tracks its `HWND` using P/Invoke (`GetWindowThreadProcessId`, `EnumWindows`) and uses `SetForegroundWindow` + `ShowWindow(SW_RESTORE)` to focus existing windows when the user clicks a session. Pros: real terminal with full feature set. Cons: more windows to manage.

Mode is configurable per-user and per-session.

### Auto-Generated READMEs

The README generation engine works in three steps:

1. **Scaffold** — on session creation, write a baseline README with frontmatter (id, label, working dir, model, created timestamp) and empty placeholder sections.
2. **Append** — as the session runs, append events to a hidden journal (`<cwd>\.copilot-session\journal.jsonl`) capturing every turn, file edit, command run, and decision point.
3. **Summarize** — on demand (or automatically every N turns), Copilot is invoked with a meta-prompt that takes the journal as input and produces updated README sections. User-edited sections (delimited by `<!-- user-edited:start -->` ... `<!-- user-edited:end -->`) are preserved verbatim.

The result: **a self-documenting session that's ready to hand off to a teammate, attach to a PR, or revisit weeks later.**

---

## Tech Stack

| Layer | Choice | Why |
|-------|--------|-----|
| Runtime | **.NET 8** | Long-term support, AOT-capable, fast |
| UI Framework | **WPF** | Most mature Windows UI framework, deep Win32 access, excellent tooling |
| MVVM | **CommunityToolkit.Mvvm** | Source generators reduce boilerplate |
| Markdown | **Markdig** | Fast, extensible Markdown → HTML for README preview |
| Markdown UI | **Neo.Markdig.Xaml** (or equivalent) | Renders Markdown directly to WPF FlowDocument |
| Persistence | **SQLite via Microsoft.Data.Sqlite** | Local, file-based, zero-config |
| Git | **LibGit2Sharp** | In-process git operations (branch detection, log) |
| GitHub | **Octokit.NET** + `gh` CLI for auth | Mature SDK, reuses existing auth |
| PowerShell | **System.Management.Automation** | Host PS runspaces in-process |
| Logging | **Serilog** | Structured logging with rolling files |
| Testing | **xUnit + Moq + FluentAssertions** | Standard .NET test stack |
| Packaging | **MSIX** + **Velopack** for auto-updates | Modern Windows packaging, signed installers |

---

## Screenshots / Mockup

An interactive HTML mockup of the UI is available at [`mockup/copilot-session-manager.html`](mockup/copilot-session-manager.html). Open it in any browser to explore:

- Dashboard with color-coded session cards (working / awaiting input / inactive)
- Token usage bars per session
- Toggle for showing inactive sessions
- Rename modal with group assignment
- Merge mode for combining session contexts
- README modal with Preview / Edit Markdown tabs
- Mock PowerShell terminal window with focus behavior

---

## Getting Started

> ⚠️ The application is currently in early V1 development. These instructions will be updated as the build matures.

### Prerequisites

- Windows 10 (1809+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [GitHub CLI (`gh`)](https://cli.github.com/) authenticated (`gh auth login`)
- [GitHub Copilot CLI](https://docs.github.com/en/copilot/github-copilot-in-the-cli) installed and authenticated
- PowerShell 7+ (`pwsh`) recommended

### Build from source

```powershell
git clone https://github.com/richardpan/copilot-session-manager.git
cd copilot-session-manager
dotnet restore
dotnet build -c Release
dotnet run --project src/CopilotSessionManager
```

### Install (once releases are published)

Download the latest MSIX installer from the [Releases](https://github.com/richardpan/copilot-session-manager/releases) page and double-click to install.

---

## Configuration

User settings are stored at `%LOCALAPPDATA%\CopilotSessionManager\settings.json`:

```json
{
  "powershellMode": "hosted",
  "defaultModel": "claude-sonnet-4.6",
  "tokenAlertThreshold": 0.75,
  "autoArchiveAfterHours": 24,
  "autoRegenerateReadmeEveryNTurns": 10,
  "githubToken": "(read from gh CLI)",
  "theme": "dark"
}
```

Most settings are also exposed in the in-app **Settings** dialog.

---

## Project Structure

```
copilot-session-manager/
├── src/
│   ├── CopilotSessionManager/          # WPF app (main project)
│   │   ├── Views/                       # XAML views
│   │   ├── ViewModels/                  # MVVM view models
│   │   ├── Models/                      # Data models
│   │   ├── Controls/                    # Custom WPF controls (terminal, etc.)
│   │   ├── App.xaml                     # Application entry
│   │   └── CopilotSessionManager.csproj
│   ├── CopilotSessionManager.Core/     # Business logic (no UI)
│   │   ├── Sessions/                    # SessionStore, lifecycle
│   │   ├── Readme/                      # README generation engine
│   │   ├── PowerShell/                  # Hosted runspace + window mgmt
│   │   ├── GitHub/                      # GitHub API integration
│   │   └── Persistence/                 # SQLite repositories
│   └── CopilotSessionManager.Native/   # Win32 P/Invoke wrappers
├── tests/
│   ├── CopilotSessionManager.Core.Tests/
│   └── CopilotSessionManager.Tests/
├── mockup/
│   └── copilot-session-manager.html    # Interactive UI mockup
├── docs/
│   ├── architecture.md
│   ├── readme-generation.md
│   └── powershell-integration.md
├── .github/
│   ├── workflows/
│   │   ├── ci.yml
│   │   └── release.yml
│   └── ISSUE_TEMPLATE/
├── CopilotSessionManager.sln
└── README.md
```

---

## Roadmap & Issues

- 🎯 **V1 Milestone:** [github.com/richardpan/copilot-session-manager/milestone/1](https://github.com/richardpan/copilot-session-manager/milestone/1)
- 📋 **All issues:** [github.com/richardpan/copilot-session-manager/issues](https://github.com/richardpan/copilot-session-manager/issues)
- 🏷️ **Labels:** `v1` · `v2` · `enhancement` · `ux` · `cost-tracking` · `collaboration` · `lifecycle` · `documentation`

---

## Contributing

Contributions are welcome! Please:

1. Open an issue first to discuss any non-trivial change
2. Fork the repo and create a topic branch
3. Follow the existing code style (`.editorconfig` enforced)
4. Add tests for new logic
5. Open a pull request referencing the issue

---

## License

MIT — see [`LICENSE`](LICENSE) for details.

---

<p align="center">
  <em>Built with ❤️ for engineers who run too many Copilot sessions at once.</em>
</p>
