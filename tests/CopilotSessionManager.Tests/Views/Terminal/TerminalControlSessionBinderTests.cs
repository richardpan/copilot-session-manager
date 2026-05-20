using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using CopilotSessionManager.Terminal.Wpf;
using CopilotSessionManager.Views.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Tests.Views.Terminal;

/// <summary>
/// STA tests for <see cref="TerminalControlSessionBinder"/> — the helper
/// that wires the embedded <c>TerminalControl</c> in
/// <c>TerminalTabsView</c> to its backing <c>TerminalSession</c>.
/// Regression tests for the bug where typing into an embedded terminal
/// did nothing and resizing the dashboard left the PTY pinned at its
/// initial 30×100.
/// </summary>
public class TerminalControlSessionBinderTests
{
    [Fact]
    public void InputProduced_PasteBytes_ForwardsToSession()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();
            var captured = new List<byte[]>();
            using var binder = new TerminalControlSessionBinder(
                control,
                bytes => captured.Add(bytes.ToArray()),
                static (_, _) => { },
                initialRows: 30,
                initialCols: 100);

            // Paste() raises InputProduced synchronously with the
            // pasted bytes (no bracketed-paste wrapping because no
            // buffer is attached).
            control.Paste("hi");

            captured.Should().ContainSingle()
                .Which.Should().Equal((byte)'h', (byte)'i');
        });
    }

    [Fact]
    public void InputProduced_MultiplePastes_AccumulateInOrder()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();
            var captured = new List<byte[]>();
            using var binder = new TerminalControlSessionBinder(
                control,
                bytes => captured.Add(bytes.ToArray()),
                static (_, _) => { },
                initialRows: 30,
                initialCols: 100);

            control.Paste("a");
            control.Paste("b");

            captured.Should().HaveCount(2);
            captured[0].Should().Equal((byte)'a');
            captured[1].Should().Equal((byte)'b');
        });
    }

    [Fact]
    public void TryApplyResize_DifferentDimensions_InvokesCallbackAndUpdatesLastApplied()
    {
        RunSta(() =>
        {
            var control = ConstructLaidOutControl(widthCells: 60, heightCells: 18);
            var resizes = new List<(int Rows, int Cols)>();
            using var binder = new TerminalControlSessionBinder(
                control,
                static _ => { },
                (rows, cols) => resizes.Add((rows, cols)),
                initialRows: 30,
                initialCols: 100);

            // Compute the expected cell count from the same control so
            // the assertion stays in sync with the cell-metrics maths.
            var (expectedRows, expectedCols) = control.CellsForViewport(new Size(control.ActualWidth, control.ActualHeight));

            var applied = binder.TryApplyResize(new Size(control.ActualWidth, control.ActualHeight));

            applied.Should().BeTrue();
            resizes.Should().ContainSingle()
                .Which.Should().Be((expectedRows, expectedCols));
            binder.LastRows.Should().Be(expectedRows);
            binder.LastCols.Should().Be(expectedCols);
        });
    }

    [Fact]
    public void TryApplyResize_SameDimensions_IsNoOp()
    {
        RunSta(() =>
        {
            var control = ConstructLaidOutControl(widthCells: 80, heightCells: 24);
            var resizes = new List<(int Rows, int Cols)>();
            var pixelSize = new Size(control.ActualWidth, control.ActualHeight);
            var (initialRows, initialCols) = control.CellsForViewport(pixelSize);

            using var binder = new TerminalControlSessionBinder(
                control,
                static _ => { },
                (rows, cols) => resizes.Add((rows, cols)),
                initialRows,
                initialCols);

            // Same pixel size → same cell count → no forward.
            var applied = binder.TryApplyResize(pixelSize);

            applied.Should().BeFalse();
            resizes.Should().BeEmpty();
            binder.LastRows.Should().Be(initialRows);
            binder.LastCols.Should().Be(initialCols);
        });
    }

    [Fact]
    public void TryApplyResize_FloorsAtTwoCells()
    {
        RunSta(() =>
        {
            var control = ConstructLaidOutControl(widthCells: 80, heightCells: 24);
            var resizes = new List<(int Rows, int Cols)>();
            using var binder = new TerminalControlSessionBinder(
                control,
                static _ => { },
                (rows, cols) => resizes.Add((rows, cols)),
                initialRows: 30,
                initialCols: 100);

            // A 1×1 pixel viewport rounds to 0 rows/0 cols which the
            // PTY rejects; the binder must clamp to (2, 2).
            binder.TryApplyResize(new Size(1, 1)).Should().BeTrue();

            resizes.Should().ContainSingle()
                .Which.Should().Be((2, 2));
            binder.LastRows.Should().Be(2);
            binder.LastCols.Should().Be(2);
        });
    }

    [Fact]
    public void Dispose_UnhooksInputProducedHandler()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();
            var captured = new List<byte[]>();
            var binder = new TerminalControlSessionBinder(
                control,
                bytes => captured.Add(bytes.ToArray()),
                static (_, _) => { },
                initialRows: 30,
                initialCols: 100);

            binder.Dispose();
            binder.IsDisposed.Should().BeTrue();

            // After Dispose, further input on the control must not
            // reach our callback.
            control.Paste("x");

            captured.Should().BeEmpty();
        });
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();
            var binder = new TerminalControlSessionBinder(
                control,
                static _ => { },
                static (_, _) => { },
                initialRows: 30,
                initialCols: 100);

            binder.Dispose();
            Action again = () => binder.Dispose();

            again.Should().NotThrow();
        });
    }

    [Fact]
    public void TryApplyResize_AfterDispose_ReturnsFalseAndDoesNotInvokeCallback()
    {
        RunSta(() =>
        {
            var control = ConstructLaidOutControl(widthCells: 80, heightCells: 24);
            var resizes = new List<(int Rows, int Cols)>();
            var binder = new TerminalControlSessionBinder(
                control,
                static _ => { },
                (rows, cols) => resizes.Add((rows, cols)),
                initialRows: 30,
                initialCols: 100);

            binder.Dispose();

            binder.TryApplyResize(new Size(control.ActualWidth, control.ActualHeight))
                .Should().BeFalse();
            resizes.Should().BeEmpty();
        });
    }

    [Fact]
    public void TryApplyResize_SwallowsObjectDisposedFromCallback_AndDoesNotAdvanceState()
    {
        RunSta(() =>
        {
            var control = ConstructLaidOutControl(widthCells: 80, heightCells: 24);
            using var binder = new TerminalControlSessionBinder(
                control,
                static _ => { },
                (_, _) => throw new ObjectDisposedException("session"),
                initialRows: 30,
                initialCols: 100);

            Action act = () => binder.TryApplyResize(new Size(control.ActualWidth, control.ActualHeight));

            act.Should().NotThrow();
            // Last-applied stays at the initial values because the
            // forward failed; a later non-disposed callback would still
            // see "we never made it to this size".
            binder.LastRows.Should().Be(30);
            binder.LastCols.Should().Be(100);
        });
    }

    [Fact]
    public void InputProduced_SwallowsObjectDisposedFromCallback()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();
            using var binder = new TerminalControlSessionBinder(
                control,
                _ => throw new ObjectDisposedException("session"),
                static (_, _) => { },
                initialRows: 30,
                initialCols: 100);

            Action act = () => control.Paste("h");

            act.Should().NotThrow();
        });
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();

            Action nullControl = () => _ = new TerminalControlSessionBinder(
                null!, static _ => { }, static (_, _) => { }, 30, 100);
            Action nullInput = () => _ = new TerminalControlSessionBinder(
                control, null!, static (_, _) => { }, 30, 100);
            Action nullResize = () => _ = new TerminalControlSessionBinder(
                control, static _ => { }, null!, 30, 100);

            nullControl.Should().Throw<ArgumentNullException>();
            nullInput.Should().Throw<ArgumentNullException>();
            nullResize.Should().Throw<ArgumentNullException>();
        });
    }

    [Fact]
    public void Constructor_NonPositiveInitialDimensions_Throw()
    {
        RunSta(() =>
        {
            var control = new TerminalControl();

            Action zeroRows = () => _ = new TerminalControlSessionBinder(
                control, static _ => { }, static (_, _) => { }, 0, 100);
            Action zeroCols = () => _ = new TerminalControlSessionBinder(
                control, static _ => { }, static (_, _) => { }, 30, 0);

            zeroRows.Should().Throw<ArgumentOutOfRangeException>();
            zeroCols.Should().Throw<ArgumentOutOfRangeException>();
        });
    }

    /// <summary>
    /// Build a <see cref="TerminalControl"/> and force WPF to assign it
    /// real layout dimensions so <see cref="TerminalControl.CellsForViewport"/>
    /// reflects a meaningful viewport rather than zero-by-zero. The
    /// requested <paramref name="widthCells"/> / <paramref name="heightCells"/>
    /// are approximate (we multiply by the default cell metrics).
    /// </summary>
    private static TerminalControl ConstructLaidOutControl(int widthCells, int heightCells)
    {
        var control = new TerminalControl();
        // Approximate cell size for Cascadia Mono 14pt at 96 DPI:
        // ~8.4 px wide × ~19 px tall. Over-allocate so the integer
        // cell count comes out at least as large as requested.
        var pixelWidth = widthCells * 12.0;
        var pixelHeight = heightCells * 24.0;
        control.Width = pixelWidth;
        control.Height = pixelHeight;
        control.Measure(new Size(pixelWidth, pixelHeight));
        control.Arrange(new Rect(0, 0, pixelWidth, pixelHeight));
        control.UpdateLayout();
        return control;
    }

    private static void RunSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            ExceptionDispatchInfo.Capture(captured).Throw();
        }
    }
}
