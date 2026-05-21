using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CopilotSessionManager.Terminal.Wpf;
using CopilotSessionManager.ViewModels.Terminal;

namespace CopilotSessionManager.Views.Terminal;

/// <summary>
/// Phase 6A scaffolding (issue #159): hosts the embedded terminal tab
/// strip. Binds to <see cref="ViewModels.Terminal.TerminalTabsViewModel"/>.
/// Phase 6C adds middle-click-to-close support via
/// <see cref="OnTabItemMouseDown"/>; the rest of the UX polish (close
/// glyph + Ctrl+Tab cycling) is driven from the XAML.
/// </summary>
/// <remarks>
/// V1.5 follow-up: hooks each <c>TerminalControl</c>'s
/// <c>InputProduced</c> and <c>SizeChanged</c> events into the backing
/// <c>TerminalSession</c> via <see cref="TerminalControlSessionBinder"/>,
/// with a per-control debounce timer for resize. Without this wiring,
/// typing into the embedded tab did nothing and resizing the dashboard
/// window left the PTY pinned at its initial 30×100.
/// </remarks>
public partial class TerminalTabsView : UserControl
{
    /// <summary>
    /// Debounce window for resize forwarding. Matches the standalone
    /// <c>TerminalWindow</c>'s 100 ms cadence — long enough to collapse
    /// drag-resize spam into a single <c>ConPTY</c> resize, short enough
    /// that the user perceives the terminal reflowing while they drag.
    /// </summary>
    internal static readonly TimeSpan ResizeDebounceInterval = TimeSpan.FromMilliseconds(120);

    // The TabControl recycles its single content presenter as the user
    // switches tabs, so each switch hands us a fresh TerminalControl
    // instance via Loaded. A weak table keeps the per-control binder /
    // debounce state alive only as long as the control itself is, with
    // no risk of leaking sessions if Unloaded somehow doesn't fire.
    private readonly ConditionalWeakTable<TerminalControl, EmbeddedTerminalState> _states = new();

    public TerminalTabsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Phase 6C (#159): middle-button click on any tab header closes
    /// that tab. The handler is attached via an <c>EventSetter</c> in
    /// the <see cref="TabControl.Resources"/> style so every header in
    /// the strip picks it up automatically. Routes through the
    /// view-model's <c>CloseTabCommand</c> so the close path is
    /// identical to the close glyph and any future external callers.
    /// </summary>
    private void OnTabItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }
        if (sender is not TabItem item || item.DataContext is not TerminalTabViewModel tab)
        {
            return;
        }
        if (DataContext is not TerminalTabsViewModel vm)
        {
            return;
        }
        if (vm.CloseTabCommand.CanExecute(tab))
        {
            vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Attach input + resize wiring + focus the control so the first
    /// keystroke lands inside the PTY. Idempotent — re-entry while the
    /// state is still alive returns immediately so a re-template doesn't
    /// double-hook handlers.
    /// </summary>
    private void OnEmbeddedTerminalLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TerminalControl control)
        {
            return;
        }
        if (control.DataContext is not TerminalTabViewModel tab)
        {
            return;
        }

        var session = tab.TerminalSession;
        var buffer = session.Buffer;
        var initialRows = buffer.Rows > 0 ? buffer.Rows : 30;
        var initialCols = buffer.Columns > 0 ? buffer.Columns : 100;

        var state = _states.GetValue(control, _ => new EmbeddedTerminalState());
        if (state.Binder is not null && !state.Binder.IsDisposed)
        {
            // Loaded fired again without Unloaded in between (can happen
            // when the tab is briefly hidden and reshown). The existing
            // binder already covers this control.
            return;
        }

        state.Binder = new TerminalControlSessionBinder(
            control,
            bytes => ForwardInput(session, bytes),
            (rows, cols) => ForwardResize(session, rows, cols),
            initialRows,
            initialCols);

        if (state.DebounceTimer is null)
        {
            state.DebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = ResizeDebounceInterval,
            };
            state.DebounceTimer.Tick += (_, _) =>
            {
                state.DebounceTimer!.Stop();
                state.Binder?.TryApplyResize(state.PendingSize);
            };
        }

        // First-fit: the tab is now visible and laid out, so the
        // rendered viewport probably disagrees with the PTY's initial
        // 30×100 (especially on tall dashboards). Defer to Loaded
        // priority so every pending Measure / Arrange pass has settled
        // before we sample ActualWidth / ActualHeight. Without the
        // deferral, tabs 2+ can pick up stale preliminary sizes because
        // the ContentPresenter re-templates on tab switch and the first
        // layout pass may not yet have propagated the final dimensions.
        var capturedState = state;
        var capturedBinder = state.Binder;
        control.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (control.ActualWidth > 0 && control.ActualHeight > 0)
                {
                    capturedState.PendingSize = new Size(control.ActualWidth, control.ActualHeight);
                    capturedBinder?.TryApplyResize(capturedState.PendingSize);
                }
            });

        // Give the control focus so the user can start typing without
        // first clicking into the terminal area. We defer to Input
        // priority so the TabControl's own focus-management (which runs
        // during layout/render) has finished before we grab focus.
        // Without the deferral, the TabControl can steal focus back to
        // the tab header, causing the first keystroke to be routed
        // elsewhere.
        control.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            () => Keyboard.Focus(control));
    }

    /// <summary>
    /// Tear down the per-control binder + debounce timer. Safe to call
    /// repeatedly (Unloaded can fire more than once during teardown).
    /// </summary>
    private void OnEmbeddedTerminalUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TerminalControl control)
        {
            return;
        }
        if (!_states.TryGetValue(control, out var state))
        {
            return;
        }

        state.DebounceTimer?.Stop();
        state.Binder?.Dispose();
        state.Binder = null;
        // Leave the DispatcherTimer attached to the state so a
        // subsequent Loaded reuses it; clearing the entry would let the
        // table re-allocate. The state itself is held by the
        // ConditionalWeakTable only as long as the control lives.
    }

    /// <summary>
    /// Stash the latest pixel size and (re)start the debounce timer.
    /// The timer's Tick will hand the most-recent size to the binder.
    /// </summary>
    private void OnEmbeddedTerminalSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TerminalControl control)
        {
            return;
        }
        if (!_states.TryGetValue(control, out var state) || state.Binder is null)
        {
            return;
        }

        state.PendingSize = e.NewSize;
        state.DebounceTimer?.Stop();
        state.DebounceTimer?.Start();
    }

    private static void ForwardInput(CopilotSessionManager.Terminal.Hosting.TerminalSession session, ReadOnlyMemory<byte> bytes)
    {
        try
        {
            session.SendInput(bytes.Span);
        }
        catch (ObjectDisposedException)
        {
            // Session already torn down — drop the bytes.
        }
        catch (System.IO.IOException)
        {
            // PTY pipe broke (typically: child exited). The session's
            // own Exited event will close the tab; nothing for us to do.
        }
    }

    private static void ForwardResize(CopilotSessionManager.Terminal.Hosting.TerminalSession session, int rows, int cols)
    {
        try
        {
            session.Resize(rows, cols);
        }
        catch (ObjectDisposedException)
        {
            // Session torn down between SizeChanged and the debounce tick.
        }
        catch (System.IO.IOException)
        {
            // ConPTY refused the resize (typically: child already exited).
        }
    }

    /// <summary>
    /// Test hook: surface the per-control binder so STA tests can
    /// assert wiring without poking ConditionalWeakTable internals.
    /// </summary>
    internal bool TryGetBinder(TerminalControl control, out TerminalControlSessionBinder? binder)
    {
        if (_states.TryGetValue(control, out var state))
        {
            binder = state.Binder;
            return binder is not null;
        }
        binder = null;
        return false;
    }

    private sealed class EmbeddedTerminalState
    {
        public TerminalControlSessionBinder? Binder;
        public DispatcherTimer? DebounceTimer;
        public Size PendingSize;
    }
}

