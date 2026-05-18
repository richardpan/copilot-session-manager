using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CopilotSessionManager.Terminal;
using FluentAssertions;

namespace CopilotSessionManager.Terminal.Tests;

public class ScreenBufferTests
{
    private static ScreenBuffer NewBuffer(int rows = 4, int columns = 8, int scrollback = 100)
        => new(rows, columns, scrollback);

    private static List<VtEvent> Parse(string ascii)
    {
        var list = new List<VtEvent>();
        var parser = new VtParser(list.Add);
        parser.Feed(Encoding.ASCII.GetBytes(ascii));
        return list;
    }

    private static string ReadRow(ScreenBuffer buf, int row)
    {
        var sb = new StringBuilder();
        for (var c = 1; c <= buf.Columns; c++)
            sb.Append(buf.GetCell(row, c).Glyph.ToString());
        return sb.ToString();
    }

    // -- construction & defaults ----------------------------------------

    [Fact]
    public void NewBufferIsAllSpaces()
    {
        var buf = NewBuffer();
        for (var r = 1; r <= buf.Rows; r++)
        {
            ReadRow(buf, r).Should().Be(new string(' ', buf.Columns));
        }
    }

    [Fact]
    public void CursorStartsAtOriginAndVisible()
    {
        var buf = NewBuffer();
        buf.CursorRow.Should().Be(1);
        buf.CursorColumn.Should().Be(1);
        buf.CursorVisible.Should().BeTrue();
        buf.UsingAlternateScreen.Should().BeFalse();
    }

    [Fact]
    public void ConstructorRejectsNonPositiveDimensions()
    {
        ((Action)(() => _ = new ScreenBuffer(0, 1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => _ = new ScreenBuffer(1, 0))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => _ = new ScreenBuffer(1, 1, -1))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetCellRejectsOutOfRangeIndices()
    {
        var buf = NewBuffer(2, 2);
        ((Action)(() => buf.GetCell(0, 1))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => buf.GetCell(1, 3))).Should().Throw<ArgumentOutOfRangeException>();
    }

    // -- printable & cursor wrap ----------------------------------------

    [Fact]
    public void PrintingAdvancesCursorAndPlacesGlyphs()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("Hi"));

        buf.GetCell(1, 1).Glyph.ToString().Should().Be("H");
        buf.GetCell(1, 2).Glyph.ToString().Should().Be("i");
        buf.CursorRow.Should().Be(1);
        buf.CursorColumn.Should().Be(3);
    }

    [Fact]
    public void DeferredWrapAtRightEdge()
    {
        var buf = NewBuffer(rows: 3, columns: 4);
        buf.ApplyAll(Parse("ABCD"));

        buf.CursorColumn.Should().Be(4);
        buf.CursorRow.Should().Be(1);
        ReadRow(buf, 1).Should().Be("ABCD");

        buf.ApplyAll(Parse("E"));
        buf.CursorRow.Should().Be(2);
        buf.CursorColumn.Should().Be(2);
        buf.GetCell(2, 1).Glyph.ToString().Should().Be("E");
    }

    [Fact]
    public void CarriageReturnCancelsPendingWrap()
    {
        var buf = NewBuffer(rows: 3, columns: 4);
        buf.ApplyAll(Parse("ABCD\rZ"));

        buf.GetCell(1, 1).Glyph.ToString().Should().Be("Z");
        buf.GetCell(1, 2).Glyph.ToString().Should().Be("B");
        buf.CursorRow.Should().Be(1);
        buf.CursorColumn.Should().Be(2);
    }

    [Fact]
    public void LineFeedAdvancesRowKeepingColumn()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("Hi\nX"));

        buf.GetCell(1, 1).Glyph.ToString().Should().Be("H");
        buf.GetCell(2, 3).Glyph.ToString().Should().Be("X");
    }

    [Fact]
    public void BackspaceMovesLeftWithoutErasing()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("AB\bC"));

        // BS moves the cursor back, then C overwrites the B.
        buf.GetCell(1, 1).Glyph.ToString().Should().Be("A");
        buf.GetCell(1, 2).Glyph.ToString().Should().Be("C");
    }

    [Fact]
    public void TabAdvancesToNextEightColumnStop()
    {
        var buf = NewBuffer(rows: 2, columns: 16);
        buf.ApplyAll(Parse("A\tB"));

        buf.GetCell(1, 1).Glyph.ToString().Should().Be("A");
        buf.GetCell(1, 9).Glyph.ToString().Should().Be("B");
    }

    // -- scrolling & scrollback -----------------------------------------

    [Fact]
    public void LineFeedAtBottomScrollsAndPushesToScrollback()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("AB\r\nCD\r\n"));
        // After the second CRLF the cursor would advance off the bottom
        // of the screen, so "AB  " scrolls into the scroll-back ring.

        buf.ScrollbackLineCount.Should().Be(1);
        buf.GetScrollbackCell(0, 1).Glyph.ToString().Should().Be("A");
        buf.GetScrollbackCell(0, 2).Glyph.ToString().Should().Be("B");

        ReadRow(buf, 1).Should().Be("CD  ");
        ReadRow(buf, 2).Should().Be("    ");
    }

    [Fact]
    public void AlternateScreenScrollsWithoutAddingToScrollback()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("\u001B[?1049h"));
        buf.ApplyAll(Parse("AB\r\nCD\r\n"));

        buf.ScrollbackLineCount.Should().Be(0);
    }

    [Fact]
    public void ScrollbackHonorsCapacity()
    {
        var buf = new ScreenBuffer(rows: 2, columns: 2, scrollbackCapacity: 2);
        buf.ApplyAll(Parse("AA\r\nBB\r\nCC\r\nDD\r\nEE\r\n"));

        buf.ScrollbackLineCount.Should().Be(2);
        // Oldest two retained: the third- and fourth-most recent rows that
        // scrolled off ("CC" and "DD"), with "EE" still on screen.
        buf.GetScrollbackCell(0, 1).Glyph.ToString().Should().Be("C");
        buf.GetScrollbackCell(1, 1).Glyph.ToString().Should().Be("D");
    }

    [Fact]
    public void ExplicitScrollUpAndDown()
    {
        var buf = NewBuffer(rows: 3, columns: 4);
        buf.ApplyAll(Parse("AAA\r\nBBB\r\nCCC"));
        buf.ApplyAll(Parse("\u001B[1S")); // scroll up 1
        ReadRow(buf, 1).Should().Be("BBB ");
        ReadRow(buf, 2).Should().Be("CCC ");
        ReadRow(buf, 3).Should().Be("    ");

        buf.ApplyAll(Parse("\u001B[1T")); // scroll down 1
        ReadRow(buf, 1).Should().Be("    ");
        ReadRow(buf, 2).Should().Be("BBB ");
        ReadRow(buf, 3).Should().Be("CCC ");
    }

    // -- cursor positioning ---------------------------------------------

    [Fact]
    public void SetCursorPositionAcceptsOneBasedRowAndColumn()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[2;3H"));
        buf.CursorRow.Should().Be(2);
        buf.CursorColumn.Should().Be(3);
    }

    [Fact]
    public void SetCursorPositionClampsToBounds()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("\u001B[99;99H"));
        buf.CursorRow.Should().Be(2);
        buf.CursorColumn.Should().Be(4);
    }

    [Fact]
    public void RelativeCursorMotionClampsAtEdges()
    {
        var buf = NewBuffer(rows: 4, columns: 8);
        buf.ApplyAll(Parse("\u001B[100A")); // up — clamps to row 1
        buf.CursorRow.Should().Be(1);
        buf.ApplyAll(Parse("\u001B[100D")); // back — clamps to col 1
        buf.CursorColumn.Should().Be(1);
        buf.ApplyAll(Parse("\u001B[100B")); // down — clamps to row 4
        buf.CursorRow.Should().Be(4);
        buf.ApplyAll(Parse("\u001B[100C")); // forward — clamps to col 8
        buf.CursorColumn.Should().Be(8);
    }

    [Fact]
    public void DectcemTogglesCursorVisibility()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[?25l"));
        buf.CursorVisible.Should().BeFalse();
        buf.ApplyAll(Parse("\u001B[?25h"));
        buf.CursorVisible.Should().BeTrue();
    }

    // -- erase ----------------------------------------------------------

    [Fact]
    public void EraseInLineToEndClearsFromCursorToEol()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("ABCDEFGH\u001B[1;3H\u001B[K"));
        // ESC[K with default 0 = erase from col 3 to end.
        ReadRow(buf, 1).Should().Be("AB      ");
    }

    [Fact]
    public void EraseInLineToStartClearsFromBolToCursor()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("ABCDEFGH\u001B[1;5H\u001B[1K"));
        ReadRow(buf, 1).Should().Be("     FGH");
    }

    [Fact]
    public void EraseInLineAllClearsRow()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("ABCDEFGH\u001B[2K"));
        ReadRow(buf, 1).Should().Be("        ");
    }

    [Fact]
    public void EraseInDisplayAllClearsEverythingButLeavesCursor()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("ABCD\r\nEFGH\u001B[2J"));
        ReadRow(buf, 1).Should().Be("    ");
        ReadRow(buf, 2).Should().Be("    ");
        // Cursor stayed where it was after the last printable.
        buf.CursorRow.Should().Be(2);
    }

    [Fact]
    public void EraseInDisplayToEndClearsBelowAndRightOfCursor()
    {
        var buf = NewBuffer(rows: 3, columns: 4);
        buf.ApplyAll(Parse("ABCD\r\nEFGH\r\nIJKL\u001B[2;3H\u001B[J"));
        ReadRow(buf, 1).Should().Be("ABCD");
        ReadRow(buf, 2).Should().Be("EF  ");
        ReadRow(buf, 3).Should().Be("    ");
    }

    [Fact]
    public void EraseInDisplayToStartClearsAboveAndLeftOfCursor()
    {
        var buf = NewBuffer(rows: 3, columns: 4);
        buf.ApplyAll(Parse("ABCD\r\nEFGH\r\nIJKL\u001B[2;3H\u001B[1J"));
        ReadRow(buf, 1).Should().Be("    ");
        ReadRow(buf, 2).Should().Be("   H");
        ReadRow(buf, 3).Should().Be("IJKL");
    }

    [Fact]
    public void EraseInDisplayScrollbackClearsHistory()
    {
        var buf = NewBuffer(rows: 2, columns: 2);
        buf.ApplyAll(Parse("AA\r\nBB\r\nCC\r\n"));
        buf.ScrollbackLineCount.Should().BeGreaterThan(0);
        buf.ApplyAll(Parse("\u001B[3J"));
        buf.ScrollbackLineCount.Should().Be(0);
    }

    // -- SGR & cell decoration ------------------------------------------

    [Fact]
    public void SgrColoursTheNewlyPrintedCell()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[31;1mR\u001B[0mN"));

        var coloured = buf.GetCell(1, 1);
        coloured.Foreground.Should().Be(TerminalColor.Indexed(1));
        coloured.Attributes.Should().HaveFlag(CellAttributes.Bold);

        var plain = buf.GetCell(1, 2);
        plain.Foreground.Should().Be(TerminalColor.Default);
        plain.Attributes.Should().Be(CellAttributes.None);
    }

    [Fact]
    public void SgrTrueColourBackground()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[48;2;10;20;30mX"));

        var cell = buf.GetCell(1, 1);
        cell.Background.Kind.Should().Be(TerminalColorKind.Rgb);
        cell.Background.Red.Should().Be(10);
        cell.Background.Green.Should().Be(20);
        cell.Background.Blue.Should().Be(30);
    }

    [Fact]
    public void StyleResetClearsForegroundAndBackground()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[31m"));
        buf.Style.Foreground.Should().Be(TerminalColor.Indexed(1));
        buf.ApplyAll(Parse("\u001B[0m"));
        buf.Style.Foreground.Should().Be(TerminalColor.Default);
    }

    // -- save / restore --------------------------------------------------

    [Fact]
    public void SaveAndRestoreCursorRoundTrip()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[2;3H\u001B[33;4m\u001B7"));
        buf.ApplyAll(Parse("\u001B[1;1H\u001B[0m"));
        buf.ApplyAll(Parse("\u001B8"));

        buf.CursorRow.Should().Be(2);
        buf.CursorColumn.Should().Be(3);
        buf.Style.Foreground.Should().Be(TerminalColor.Indexed(3));
        buf.Style.Attributes.Should().HaveFlag(CellAttributes.Underline);
    }

    [Fact]
    public void RestoreWithoutSaveResetsToOriginAndDefaults()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[2;3H\u001B[31m"));
        buf.ApplyAll(Parse("\u001B8"));

        buf.CursorRow.Should().Be(1);
        buf.CursorColumn.Should().Be(1);
        buf.Style.Foreground.Should().Be(TerminalColor.Default);
    }

    // -- alternate screen ------------------------------------------------

    [Fact]
    public void AlternateScreenIsClearedOnEntryAndPrimaryIsPreserved()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("ABCD\r\nEFGH"));
        buf.ApplyAll(Parse("\u001B[?1049h"));
        ReadRow(buf, 1).Should().Be("    ");
        ReadRow(buf, 2).Should().Be("    ");

        buf.ApplyAll(Parse("\u001B[?1049l"));
        ReadRow(buf, 1).Should().Be("ABCD");
        ReadRow(buf, 2).Should().Be("EFGH");
    }

    [Fact]
    public void AlternateScreenHasItsOwnSaveSlot()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("\u001B[2;2H\u001B7"));
        buf.ApplyAll(Parse("\u001B[?1049h"));
        // Restore on alternate screen: no save was taken on alternate,
        // so it should reset to origin, not jump to (2,2).
        buf.ApplyAll(Parse("\u001B8"));
        buf.CursorRow.Should().Be(1);
        buf.CursorColumn.Should().Be(1);
    }

    // -- dirty tracking --------------------------------------------------

    [Fact]
    public void DirtyRowsTrackUpdates()
    {
        var buf = NewBuffer();
        buf.ClearDirty();
        buf.HasDirtyRows.Should().BeFalse();

        buf.ApplyAll(Parse("X"));
        buf.HasDirtyRows.Should().BeTrue();
        buf.DirtyRows[0].Should().BeTrue();
        buf.DirtyRows[1].Should().BeFalse();

        buf.ClearDirty();
        buf.HasDirtyRows.Should().BeFalse();
    }

    [Fact]
    public void ScrollMarksAllRowsDirty()
    {
        var buf = NewBuffer(rows: 2, columns: 2);
        buf.ApplyAll(Parse("AA\nBB"));
        buf.ClearDirty();

        buf.ApplyAll(Parse("\n"));
        buf.DirtyRows.Should().AllBeEquivalentTo(true);
    }

    // -- resize ---------------------------------------------------------

    [Fact]
    public void ResizeWiderPadsRowsWithBlanks()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("ABCD\r\nEFGH"));
        buf.Resize(2, 6);

        buf.Columns.Should().Be(6);
        ReadRow(buf, 1).Should().Be("ABCD  ");
        ReadRow(buf, 2).Should().Be("EFGH  ");
    }

    [Fact]
    public void ResizeNarrowerTruncatesRows()
    {
        var buf = NewBuffer(rows: 2, columns: 4);
        buf.ApplyAll(Parse("ABCD\r\nEFGH"));
        buf.Resize(2, 2);

        buf.Columns.Should().Be(2);
        ReadRow(buf, 1).Should().Be("AB");
        ReadRow(buf, 2).Should().Be("EF");
    }

    [Fact]
    public void ResizeTallerKeepsContentNearTop()
    {
        var buf = NewBuffer(rows: 2, columns: 2);
        buf.ApplyAll(Parse("AB\r\nCD"));
        buf.Resize(4, 2);

        buf.Rows.Should().Be(4);
        ReadRow(buf, 1).Should().Be("AB");
        ReadRow(buf, 2).Should().Be("CD");
        ReadRow(buf, 3).Should().Be("  ");
        ReadRow(buf, 4).Should().Be("  ");
    }

    [Fact]
    public void ResizeClampsCursorIntoNewBounds()
    {
        var buf = NewBuffer(rows: 4, columns: 8);
        buf.ApplyAll(Parse("\u001B[4;8H"));
        buf.Resize(2, 4);

        buf.CursorRow.Should().Be(2);
        buf.CursorColumn.Should().Be(4);
    }

    // -- reset / OSC / paste --------------------------------------------

    [Fact]
    public void ResetTerminalRestoresEverything()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[31mABC\n\u001B[?25l\u001B[?2004h"));
        buf.ApplyAll(Parse("\u001Bc"));

        buf.CursorRow.Should().Be(1);
        buf.CursorColumn.Should().Be(1);
        buf.CursorVisible.Should().BeTrue();
        buf.BracketedPasteEnabled.Should().BeFalse();
        buf.Style.Foreground.Should().Be(TerminalColor.Default);
        ReadRow(buf, 1).Should().Be(new string(' ', buf.Columns));
    }

    [Fact]
    public void OscWindowTitleIsCaptured()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B]0;Hello\u0007"));
        buf.WindowTitle.Should().Be("Hello");
    }

    [Fact]
    public void BracketedPasteFlagTracksMode()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[?2004h"));
        buf.BracketedPasteEnabled.Should().BeTrue();
        buf.ApplyAll(Parse("\u001B[?2004l"));
        buf.BracketedPasteEnabled.Should().BeFalse();
    }

    // -- #177: DECCKM / application cursor keys -------------------------

    [Fact]
    public void ApplicationCursorKeysFlagDefaultsToFalse()
    {
        var buf = NewBuffer();
        buf.ApplicationCursorKeys.Should().BeFalse();
    }

    [Fact]
    public void ApplicationCursorKeysFlagTracksDecMode1()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[?1h"));
        buf.ApplicationCursorKeys.Should().BeTrue();
        buf.ApplyAll(Parse("\u001B[?1l"));
        buf.ApplicationCursorKeys.Should().BeFalse();
    }

    [Fact]
    public void ApplicationCursorKeysChangedFiresOnTransitionsOnly()
    {
        var buf = NewBuffer();
        var changes = 0;
        buf.ApplicationCursorKeysChanged += (_, _) => changes++;

        buf.ApplyAll(Parse("\u001B[?1h"));
        changes.Should().Be(1);

        // Idempotent set: no transition, no event.
        buf.ApplyAll(Parse("\u001B[?1h"));
        changes.Should().Be(1);

        buf.ApplyAll(Parse("\u001B[?1l"));
        changes.Should().Be(2);
    }

    [Fact]
    public void ResetClearsApplicationCursorKeysAndFiresEvent()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[?1h"));
        buf.ApplicationCursorKeys.Should().BeTrue();

        var changes = 0;
        buf.ApplicationCursorKeysChanged += (_, _) => changes++;

        buf.ApplyAll(Parse("\u001Bc"));

        buf.ApplicationCursorKeys.Should().BeFalse();
        changes.Should().Be(1);
    }

    [Fact]
    public void ApplicationCursorKeysFlagFlipsOnBothPrimaryAndAlternateScreens()
    {
        var buf = NewBuffer();
        buf.ApplyAll(Parse("\u001B[?1h"));
        buf.ApplicationCursorKeys.Should().BeTrue();

        // Enter the alternate screen, flip DECCKM off, leave alt.
        buf.ApplyAll(Parse("\u001B[?1049h"));
        buf.UsingAlternateScreen.Should().BeTrue();
        buf.ApplyAll(Parse("\u001B[?1l"));
        buf.ApplicationCursorKeys.Should().BeFalse();

        buf.ApplyAll(Parse("\u001B[?1049l"));
        buf.UsingAlternateScreen.Should().BeFalse();
        // DECCKM is screen-independent (per xterm) - the flag we tracked
        // last is what sticks.
        buf.ApplicationCursorKeys.Should().BeFalse();
    }

    // -- realistic scenario --------------------------------------------

    [Fact]
    public void RealisticPromptRedrawProducesExpectedScreen()
    {
        var buf = NewBuffer(rows: 4, columns: 16);
        // Clear, home, set cyan bold, print prompt, reset, print user text.
        buf.ApplyAll(Parse("\u001B[2J\u001B[H\u001B[36;1mCopilot> \u001B[0mhello"));

        ReadRow(buf, 1).Should().StartWith("Copilot> hello");
        var prompt = buf.GetCell(1, 1);
        prompt.Foreground.Should().Be(TerminalColor.Indexed(6));
        prompt.Attributes.Should().HaveFlag(CellAttributes.Bold);

        var user = buf.GetCell(1, 10);
        user.Foreground.Should().Be(TerminalColor.Default);
        user.Attributes.Should().Be(CellAttributes.None);
    }

    [Fact]
    public void ApplyAllRejectsNullEnumerable()
    {
        var buf = NewBuffer();
        ((Action)(() => buf.ApplyAll(null!))).Should().Throw<ArgumentNullException>();
    }
}
