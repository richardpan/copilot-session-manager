# Manual Accessibility Smoke Test

This is the human-driven accessibility smoke-test checklist for Copilot Session
Manager. It complements the general [`manual-tests.md`](manual-tests.md) plan
and exists to verify the work landed in the accessibility audit pass
(PR [#98], closing [#45]).

> Automated unit coverage exists under
> [`tests/CopilotSessionManager.Tests/Accessibility/`](../tests/CopilotSessionManager.Tests/Accessibility/)
> for `AutomationProperties.Name`, badge / status glyphs, and the focus-visual
> resource. Everything below covers behaviour we deliberately do **not**
> automate: real screen-reader announcements, real keyboard focus paint, and
> human perception of color contrast.

## Purpose & scope

This checklist verifies that:

- Narrator (and optionally NVDA) announces every interactive control with a
  meaningful name and, where relevant, a help text.
- Status and state are conveyed by **shape / glyph in addition to color**
  (paired indicators), so the app remains usable under color-vision
  deficiencies.
- Every interactive control is reachable and operable via keyboard alone,
  with a visible focus indicator (the `A11yFocusVisual` style added in #98).
- Accessibility Insights for Windows FastPass returns no failures on the
  four primary windows (dashboard, Add Issue, Merge wizard, onboarding).
- WCAG 2.1 AA contrast ratios hold for the default theme.

This checklist explicitly does **NOT** verify:

- Full Windows high-contrast theme exhaustive testing across all five
  built-in themes (Black, White, Aquatic, Desert, Dusk) — that is the
  charter of [#97]. The brush plumbing is in place after [#95] / [#128];
  this checklist confirms the default Catppuccin theme.

## Pre-conditions

- Windows 10 21H2+ or Windows 11.
- PowerShell 7+ available on `PATH`.
- A Release-configuration build of the app
  (`dotnet build CopilotSessionManager.sln -c Release`) launched from the
  build output, or the installed MSIX / MSI if one is being smoke-tested.
- Narrator: built into Windows. Toggle with **Win + Ctrl + Enter**.
- NVDA (optional): <https://www.nvaccess.org/download/>.
- Accessibility Insights for Windows:
  <https://accessibilityinsights.io/docs/windows/overview/>.
- Color-blind simulator (one of):
  - Color Oracle (free, system-wide): <https://colororacle.org/>
  - Windows built-in: **Settings → Accessibility → Color filters**.
- Color-contrast checker (one of):
  - TPGi Colour Contrast Analyser:
    <https://www.tpgi.com/color-contrast-checker/>
  - Accessibility Insights Color Contrast inspector.

## How to use this document

Same convention as [`manual-tests.md`](manual-tests.md):

- Copy a fresh copy of this file into the PR description (or a release
  checklist issue) and tick boxes as you go.
- At the top, record:
  - Build SHA tested
  - Windows build (`winver`)
  - Narrator / NVDA version
  - Accessibility Insights for Windows version
- File a bug for any unchecked / failed item before merging the PR. Label
  new bugs `accessibility, bug` and link them back to your run of this
  checklist.

---

## 1. Narrator smoke test

Launch the app, then toggle Narrator with **Win + Ctrl + Enter**. Use
**Caps Lock + arrow keys** for scan navigation; use **Tab** / **Shift+Tab**
for focus navigation.

### 1.1 Dashboard (MainWindow)

- [ ] Each session card announces its name **and** status
      (e.g. *"Session 'Foo', Active"*).
- [ ] The status glyph is announced in addition to the color (paired
      indicator acceptance criterion from #98 — `StatusGlyph` on
      `SessionCardViewModel`).
- [ ] PR / branch action buttons announce their action (e.g.
      *"Open pull request"*, *"Open branch"*) via `AutomationProperties.Name`.
- [ ] Issue link badges announce: issue number, state (open / closed /
      merged), and parsed-vs-manual origin (per #71) via `BadgeGlyph`
      on `IssueLinkViewModel`.

### 1.2 Add Issue dialog (`Views/AddIssueDialog.xaml`)

- [ ] Each input field announces its label.
- [ ] The OK button announces and is `IsDefault` (Enter activates it).
- [ ] The Cancel button announces and is `IsCancel` (Esc activates it).

### 1.3 Merge wizard (`Views/MergeWizard.xaml`)

- [ ] Each wizard step announces its title **and** position
      (e.g. *"Step 2 of 3, Choose target"*).
- [ ] Source and target session pickers announce the selected item.
- [ ] Next / Back / Finish / Cancel buttons announce their action.

### 1.4 Onboarding window

Pre-condition: delete `%LOCALAPPDATA%\CopilotSessionManager` so the app
behaves as a brand-new install.

- [ ] Each prerequisite badge announces both the name **and** the
      detected / missing status (not by color alone).
- [ ] *"Open install instructions"* links are reachable via `Tab` and
      announce as links.

## 2. NVDA smoke test (optional but recommended)

Run the same checklist as section 1 with NVDA active instead of Narrator.

- [ ] All section 1 boxes also pass under NVDA.
- [ ] Note any divergences. NVDA is generally more verbose than Narrator
      (it announces role + state more aggressively); that is expected and
      not a failure as long as the underlying name / state is correct.

## 3. Keyboard-only navigation test

Unplug the mouse (or commit to not touching it) for the entire run.

- [ ] Every interactive control on the dashboard is reachable via
      `Tab` / `Shift+Tab`.
- [ ] Tab order is logical: top-to-bottom, left-to-right within each
      section.
- [ ] The focus visual is **always** visible — no control swallows focus
      silently. The `A11yFocusVisual` style in `App.xaml` should paint a
      distinct outline on every focusable element.
- [ ] **Enter** and **Space** activate buttons.
- [ ] **Esc** cancels every dialog (Add Issue, Merge wizard, etc.).
- [ ] No keyboard traps: from any control you can `Tab` away without
      using the mouse.
- [ ] **Up / Down arrow keys** move focus between rows in the sessions
      `DataGrid`. Navigation **cycles** at the top and bottom (Up at the
      first row wraps to the last, Down at the last row wraps to the
      first). Originally tracked under [#96], shipped under V1.1.

## 4. Accessibility Insights for Windows

Install Accessibility Insights from <https://accessibilityinsights.io/>.

- [ ] Run **FastPass** on **MainWindow** → no failures.
- [ ] Run **FastPass** on the **Add Issue dialog** → no failures.
- [ ] Run **FastPass** on the **Merge wizard** → no failures.
- [ ] Run **FastPass** on the **Onboarding window** → no failures.
- [ ] Spot-check tab stops on each window using the **Tab Stops**
      visualisation; the path should match the logical reading order
      verified in section 3.
- [ ] Use the **Inspect** / **Show element details** flyout to confirm
      `Name`, `HelpText`, `LocalizedControlType`, and `IsKeyboardFocusable`
      are populated for each interactive element on at least one card and
      one badge.
- [ ] File any new failures as fresh GitHub issues labeled
      `accessibility, bug` and link them back to this checklist run.

## 5. Color contrast verification

Use Accessibility Insights' Color Contrast inspector (or the TPGi tool) to
sample foreground/background pairs.

- [ ] Verify text-on-background pairs in the dashboard meet WCAG 2.1 AA:
      **4.5:1** for normal text, **3:1** for large text (18pt+ or 14pt
      bold) and for non-text UI components / focus indicators.
- [ ] Verify the `#7F849C` separator/secondary-text color holds its
      expected ratio (~6:1 against the default surface) in practice.
- [ ] **Note:** full Windows high-contrast theme support is tracked under
      [#95] and is OUT of scope for this checklist. Do not file
      high-contrast bugs against this run; add notes there instead.

## 6. Color-blind simulation

Run Color Oracle (or use the Windows built-in color filter) and toggle
each mode in turn. For each, walk the dashboard and the issue-links panel.

- [ ] **Deuteranopia** (red-green; ~6% of men). Session status remains
      distinguishable via the status glyph.
- [ ] **Protanopia** (red-green; ~2%). Session status remains
      distinguishable.
- [ ] **Tritanopia** (blue-yellow; rare). Session status remains
      distinguishable.
- [ ] **Greyscale**. Session status remains distinguishable — this is
      the strictest test that color is no longer load-bearing.
- [ ] Issue badges (open vs closed vs PR-merged) remain distinguishable
      via shape / glyph (`BadgeGlyph` from #98), not just color, in every
      mode above.

## 7. Reporting results

When the checklist run is complete:

1. Open a comment on issue [#97] with:
   - Build SHA tested
   - Windows build (`winver`)
   - Narrator / NVDA / Accessibility Insights versions
   - The full checked-off list (copy-paste from this file).
2. For every unchecked box, file a new issue labeled `accessibility, bug`,
   describe the failure, and link the comment from step 1.
3. Issue [#97] stays **open** until a clean run (all boxes checked, or
   every failure has a tracking issue) is posted by a human reviewer.

## 8. References

- PR [#98] — accessibility audit pass (the work this checklist verifies).
- Issue [#45] — original accessibility tracking issue (closed by #98).
- V1.1 follow-ups (now shipped):
  - [#95] / PR [#128] — named brushes + Windows high-contrast theme.
  - [#96] — arrow-key navigation between dashboard rows (DataGrid `Cycle`).
- WCAG 2.1 quick reference (filter to AA):
  <https://www.w3.org/WAI/WCAG21/quickref/?levels=a%2Caa>
- Microsoft accessibility guidance for WPF / UI Automation:
  <https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/accessibility-overview>
- UI Automation overview:
  <https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiautocore-overview>
- Companion smoke tests: [`manual-tests.md`](manual-tests.md).

---

## Sign-off

| Field                            | Value |
|----------------------------------|-------|
| Tested SHA                       |       |
| Tester                           |       |
| Windows build                    |       |
| Narrator version                 |       |
| NVDA version (if used)           |       |
| Accessibility Insights version   |       |
| Color-blind simulator used       |       |
| Date (UTC)                       |       |
| Notes                            |       |

[#45]: https://github.com/richardpan/copilot-session-manager/issues/45
[#95]: https://github.com/richardpan/copilot-session-manager/issues/95
[#96]: https://github.com/richardpan/copilot-session-manager/issues/96
[#97]: https://github.com/richardpan/copilot-session-manager/issues/97
[#98]: https://github.com/richardpan/copilot-session-manager/pull/98
