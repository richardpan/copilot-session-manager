# ADR-0008 — Tabbed multi-session terminal view

**Status:** Accepted (2026-05-15).
**Context:** Issue [#159](https://github.com/richardpan/copilot-session-manager/issues/159). Sibling to epic [#93](https://github.com/richardpan/copilot-session-manager/issues/93) (ConPTY embedded terminal). Builds on [ADR-0001](./0001-conpty-for-embedded-terminal.md), [ADR-0006](./0006-vt-parser-choice.md), and [ADR-0007](./0007-wpf-terminal-rendering.md). Depends on the Phase 1–3 stack now on `main` (PRs #161, #163, #165–#168, #169, #171–#175): `PseudoConsole`, `VtParser`, `ScreenBuffer`, `TerminalControl`, and the `TerminalSession` façade.

Power users routinely keep five or more Copilot sessions in flight. The existing **▶ Open Terminal** affordance spawns a fresh top-level PowerShell window per session and re-focuses it via Win32 `SetForegroundWindow`. Taskbar sprawl makes context switching expensive, and there is no way to glance at two related sessions without alt-tabbing across windows. This ADR records how we wire the embedded-terminal pieces shipped in #93 Phase 3 into an in-app tab strip so multiple sessions render side-by-side inside the dashboard.

## Decision

We will host the tabbed terminal surface as a **WPF `TabControl` inside a dedicated `TerminalTabsView` `UserControl`**. Each tab owns one `TerminalSession` (which in turn owns its `PseudoConsole`) and presents it through one `TerminalControl`. The view binds to a `TerminalTabsViewModel` that exposes an `ObservableCollection<TerminalTabViewModel>` plus an `ActiveTab` selection; each `TerminalTabViewModel` is the durable per-session record (id, display name, tier accent colour, `TerminalSession` reference, `IsActive` flag).

The `TerminalTabsView` will live in the existing `CopilotSessionManager` WPF assembly under `Views/`. View-models go in `ViewModels/Terminal/` so they sit alongside the existing dashboard view-models without bloating the root namespace. The view-models depend only on `CopilotSessionManager.Terminal.Hosting` (the WPF-free façade library) and `CopilotSessionManager.Terminal.Wpf` (for the `TerminalControl` type binding), so they remain unit-testable behind a `TerminalSession` factory injected by the host.

Layout: the tabs view docks under the existing sessions `DataGrid` inside a `GridSplitter` row that defaults to 320 dp tall and collapses to zero when the collection is empty. The dashboard remains the primary surface; the tabs strip is additive.

## Decision drivers

- **Reuse over reinvention.** `TerminalSession` already encapsulates ConPTY + parser + buffer. `TerminalControl` is a standard WPF `Control`. A `TabControl` is the smallest possible composition that satisfies #159.
- **Per-tab session ownership.** Each tab must be able to dispose its session cleanly without taking neighbours with it.
- **MVVM, testable.** Tab management (add, activate, close, find-by-session) is pure view-model logic; the view should be a thin adapter.
- **No new dependencies.** WPF ships `TabControl` and `GridSplitter`; everything else exists.
- **Backwards compatibility.** The legacy "Open in external PowerShell window" path must remain reachable behind a setting or a `Detach` affordance so users on the V1.3 workflow are not forced into the new surface.

## Considered options

### Option A — Stock `TabControl` inside a docked `UserControl`

| Datapoint | Finding |
|---|---|
| API surface | `System.Windows.Controls.TabControl` + `TabItem`. We template the headers; the content panel hosts `TerminalControl`. |
| Per-tab isolation | Each `TabItem.Content` is a separate `TerminalControl` bound to its own `ScreenBuffer`. No shared mutable state. |
| Keyboard / mouse affordances | `TabControl` already supports `Ctrl+Tab` cycling and arrow-key header navigation; we add middle-click close and the close `×` glyph in the header template. |
| MVVM fit | `ItemsSource` + `SelectedItem` bind cleanly to `Tabs` + `ActiveTab`. |
| Disposal | We subscribe to `Tabs.CollectionChanged` and dispose departing tabs' sessions. |
| Effort | Low — one user control, one template. |

### Option B — Custom `TabbedDocumentHost` (AvalonDock-style)

| Datapoint | Finding |
|---|---|
| API surface | A custom docking surface that supports drag-to-reorder, drag-out-to-window, side-by-side splits. |
| Per-tab isolation | Same as Option A, but with more chrome. |
| Effort | Moderate-to-high — we would need to recreate or vendor a docking framework. AvalonDock as a dependency would be acceptable but adds a vendored binary and packaging tax. |
| Required by acceptance criteria? | No. #159 explicitly defers drag-to-reorder and persistence to follow-ups. |

### Option C — One terminal pane with a session switcher dropdown

| Datapoint | Finding |
|---|---|
| API surface | A `ComboBox` selects the active session; the terminal pane is a single `TerminalControl` reparented as the selection changes. |
| Multi-tab visibility | Only one session visible at a time. Defeats the explicit acceptance criterion "Multiple tabs render Copilot CLI TUIs simultaneously without flicker" (a freshly-activated terminal would flash with the alternate screen replay). |
| Effort | Low, but does not meet #159's UX shape. |

## Decision and rationale

We chose **Option A — stock `TabControl` inside a docked `UserControl`** because:

- It maps 1-to-1 onto the existing primitives (`TerminalSession`, `TerminalControl`) without adding a vendor framework.
- The acceptance criteria in #159 do not require drag-to-reorder, splits, or persisted layouts; choosing Option B today is YAGNI.
- Keyboard cycling, header templates, and selected-item binding are all out-of-the-box behaviours.
- It leaves us free to migrate to a docking framework later if we ship `Detach to external window` (#93 Phase 5) and decide the UX demands it. Migration cost is contained because the view-model layer already isolates the tab model from any specific `ItemsControl`.

## Out of scope (deferred follow-ups)

- **Drag-to-reorder tabs** — #159 calls this out as a follow-up; we will file a small issue if engineers ask for it post-MVP.
- **Persisted tab layout across restarts** — same.
- **Detach a tab back into an external window** — covered by #93 Phase 5.
- **Tab-strip overflow with kebab menu** — defer until we see real users with 10+ tabs.
- **Right-click tab context menu (Close, Close Others, Close All, Detach)** — file a follow-up if the keyboard / middle-click flow is not enough.

## Consequences

### Positive

- Reuses the entire Phase 1–3 stack with no per-tab plumbing.
- Each tab is fully isolated; closing one is safe.
- View-model logic is testable in xUnit STA tests with a fake session factory.
- Backwards-compatible: external launcher remains reachable via setting / Detach.

### Negative

- Stock `TabControl` does not natively support drag-to-reorder or close-with-middle-click; we implement those by hand in the view code-behind.
- The terminal pane competes with the dashboard `DataGrid` for vertical space. We use a `GridSplitter` so users can collapse the panel; the default split is biased toward the grid so first-run does not surprise existing users.

### Neutral

- The legacy `PowerShellSessionLauncher` stays on disk and stays wired; it will be reached via the new "Detach" affordance once #93 Phase 5 lands.

## Notes

- View-model layout: `ViewModels/Terminal/TerminalTabsViewModel.cs` + `TerminalTabViewModel.cs` (plural plus singular) so future tests can target each independently.
- Dependency-injection surface: register `TerminalTabsViewModel` as a singleton (one tabs surface per app); inject an `ITerminalSessionFactory` so tests do not need a real ConPTY.
- This ADR documents Phase 6A of #159. Phases 6B–6D (Open-in-tab integration, UX polish, two-way card↔tab sync) inherit these decisions; no further ADRs anticipated for #159.
