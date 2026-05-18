using CopilotSessionManager.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Terminal.Tests;

public class TerminalSelectionTests
{
    [Fact]
    public void IsEmpty_returns_true_when_anchor_equals_focus()
    {
        new TerminalSelection(3, 5, 3, 5).IsEmpty.Should().BeTrue();
        new TerminalSelection(3, 5, 3, 6).IsEmpty.Should().BeFalse();
        new TerminalSelection(3, 5, 4, 5).IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Normalize_returns_same_instance_when_already_in_reading_order()
    {
        var sel = new TerminalSelection(2, 3, 4, 7);
        sel.Normalize().Should().BeSameAs(sel);
    }

    [Fact]
    public void Normalize_swaps_endpoints_when_focus_precedes_anchor_across_rows()
    {
        var sel = new TerminalSelection(5, 2, 3, 8);
        sel.Normalize().Should().Be(new TerminalSelection(3, 8, 5, 2));
    }

    [Fact]
    public void Normalize_swaps_endpoints_on_same_row_when_focus_column_smaller()
    {
        var sel = new TerminalSelection(4, 9, 4, 2);
        sel.Normalize().Should().Be(new TerminalSelection(4, 2, 4, 9));
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var sel = new TerminalSelection(10, 3, 1, 9);
        sel.Normalize().Normalize().Should().Be(sel.Normalize());
    }

    [Fact]
    public void Mode_defaults_to_Stream()
    {
        new TerminalSelection(1, 1, 2, 2).Mode.Should().Be(SelectionMode.Stream);
    }

    [Fact]
    public void Mode_round_trips_via_with_init()
    {
        var sel = new TerminalSelection(1, 1, 2, 2) { Mode = SelectionMode.Rectangle };
        sel.Mode.Should().Be(SelectionMode.Rectangle);

        var moved = sel with { FocusRow = 5, FocusColumn = 7 };
        moved.Mode.Should().Be(SelectionMode.Rectangle);
    }

    [Fact]
    public void Normalize_preserves_Mode()
    {
        var sel = new TerminalSelection(5, 2, 3, 8) { Mode = SelectionMode.Rectangle };
        sel.Normalize().Mode.Should().Be(SelectionMode.Rectangle);
    }
}
