# Manual Test Plan

This is the human-driven smoke-test checklist for Copilot Session Manager.
Run through it before tagging a release and whenever a PR touches one of the
named subsystems below.

> Automated unit / integration coverage is enforced by CI (see
> [`coverlet.runsettings`](../coverlet.runsettings) and
> [`.github/workflows/ci.yml`](../.github/workflows/ci.yml)). The scenarios
> here cover behaviour that we deliberately do **not** automate yet:
> WPF rendering, real terminal launch, real network failures, and
> single-instance OS interaction. Tracking issue: [#49].

## How to use this document

- Copy a fresh copy of the relevant section into the PR description (or a
  release checklist issue) and tick boxes as you go.
- Note the build SHA and Windows version at the top.
- File a bug for any unchecked / failed item before merging the PR.

---

## 1. First launch (onboarding)

Pre-condition: delete or rename `%LOCALAPPDATA%\CopilotSessionManager` so the
app behaves as a brand-new install.

- [ ] App launches without crashing.
- [ ] Onboarding window appears on first run.
- [ ] Each prerequisite (PowerShell 7+, GitHub CLI, Copilot CLI, git) shows
      an accurate detected / missing badge.
- [ ] "Open install instructions" links work for any missing prerequisite.
- [ ] Closing onboarding transitions to the dashboard and persists the
      "onboarding complete" flag (relaunch should skip it).

## 2. Terminal / PowerShell launch flow

- [ ] Selecting a session and clicking **Open in terminal** spawns
      `pwsh.exe` (or `powershell.exe` fallback) in the session's workspace.
- [ ] The launched shell starts in the correct working directory.
- [ ] Killing the shell window updates the session's status within the next
      poll cycle.
- [ ] Launching twice in quick succession does not produce a zombie
      process (check Task Manager).

## 3. Network outage behaviour (GitHub link enrichment)

Pre-condition: disable the network adapter, or block `api.github.com` and
`github.com` via the `hosts` file.

- [ ] App still launches and shows local sessions.
- [ ] Branch / PR enrichment columns render a non-fatal "unavailable" state
      (no red dialog, no crash).
- [ ] Re-enabling the network and refreshing populates branch / PR data
      without requiring a restart.
- [ ] No infinite-retry loop visible in `--verbose` log output.

### 3a. Offline banner (#84)

- [ ] Disable the network adapter; within roughly one availability poll
      cycle (~30 s), the **amber offline banner** appears at the top of
      the main window with the GitHub status message.
- [ ] Re-enable the adapter; the offline banner auto-dismisses on the
      next poll without any user action.
- [ ] A screen reader (Narrator) announces the banner when it appears,
      because of `AutomationProperties.LiveSetting="Polite"`.

### 3b. Unauthenticated banner (#84)

Pre-condition: network is online; run `gh auth logout` in PowerShell so
the GitHub CLI is installed but signed-out.

- [ ] The **red unauthenticated banner** appears (visually distinct from
      the amber offline banner) and tells the tester to run
      `gh auth login`.
- [ ] Hovering a session card's PR badge shows a tooltip explaining
      `gh auth login` instead of the usual PR description.
- [ ] Hovering the branch hyperlink shows the same auth tooltip.
- [ ] Clicking a PR badge while unauthenticated does not crash; the
      browser may open but the underlying enrichment stays empty until
      the CLI is re-authenticated. (Note the actual current behaviour in
      the sign-off table below.)
- [ ] Run `gh auth login` and re-authenticate; the banner clears on the
      next availability poll without a restart.
- [ ] The PR / branch tooltips revert to their normal text once the
      banner clears.

## 4. Missing Copilot CLI degradation

Pre-condition: temporarily rename `copilot.exe` on `PATH` (e.g. via a
temporary `PATH` override in the launch shell).

- [ ] App launches.
- [ ] Sessions list shows whatever is on disk without throwing.
- [ ] A clear, single banner / status message indicates Copilot CLI is
      missing and links to the install docs.
- [ ] Restoring `copilot.exe` and refreshing recovers full functionality.

## 5. Single-instance second-launch focus

- [ ] Launch the app. Confirm it appears in the taskbar.
- [ ] While it is running, double-click the shortcut / re-run the
      executable from a second shell.
- [ ] The existing window is brought to the foreground (not minimised, not
      behind other windows).
- [ ] No second process appears in Task Manager.
- [ ] Closing the original window allows a fresh instance to start.

## 6. Log bundle creation

- [ ] **Help → Create log bundle** (or the equivalent menu / command)
      produces a `.zip` in the chosen location.
- [ ] The zip contains current and rolled log files.
- [ ] No GitHub tokens, PATs, user home directory paths in clear text, or
      session content appears in the bundle (spot-check 2-3 files).
- [ ] Bundle filename includes the app version and a UTC timestamp.

## 7. Verbose-logging toggle

- [ ] Enabling verbose logging from the UI / settings flips Serilog's
      minimum level to `Debug` without restart.
- [ ] New log entries reflect the increased verbosity.
- [ ] Disabling it returns to `Information` and stops emitting `Debug`
      lines.
- [ ] The setting persists across a relaunch.

## 8. README open

- [ ] Selecting a session and triggering **Open README** opens the
      session's `README.md` in the OS-default markdown handler (or browser
      fallback).
- [ ] If the README is missing, the app shows a non-fatal message rather
      than crashing.
- [ ] The action works for sessions with paths containing spaces and
      Unicode characters.

---

## Sign-off

| Field         | Value |
|---------------|-------|
| Tested SHA    |       |
| Tester        |       |
| Windows build |       |
| Date (UTC)    |       |
| Notes         |       |

[#49]: https://github.com/richardpan/copilot-session-manager/issues/49
