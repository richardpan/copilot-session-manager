using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CopilotSessionManager.Terminal;
using FluentAssertions;
using static CopilotSessionManager.Terminal.Tests.ParseHelpers;

namespace CopilotSessionManager.Terminal.Tests;

public class VtParserTests
{
    // -- Printable & C0 controls -----------------------------------------

    [Fact]
    public void PrintableAsciiEmitsOnePrintRunePerByte()
    {
        var events = ParseAll("Hi");

        events.Should().HaveCount(2);
        events[0].Should().Be(new PrintRune(new Rune('H')));
        events[1].Should().Be(new PrintRune(new Rune('i')));
    }

    [Fact]
    public void DelByteIsIgnoredInGround()
    {
        var events = ParseAll("a\u007Fb");

        events.Should().Equal(
            new PrintRune(new Rune('a')),
            new PrintRune(new Rune('b')));
    }

    [Fact]
    public void NulByteIsSilentlyDropped()
    {
        var events = ParseAll("a\0b");

        events.Should().Equal(
            new PrintRune(new Rune('a')),
            new PrintRune(new Rune('b')));
    }

    [Theory]
    [InlineData((byte)0x07, typeof(RingBell))]
    [InlineData((byte)0x08, typeof(Backspace))]
    [InlineData((byte)0x09, typeof(HorizontalTab))]
    [InlineData((byte)0x0A, typeof(LineFeed))]
    [InlineData((byte)0x0B, typeof(LineFeed))]
    [InlineData((byte)0x0C, typeof(LineFeed))]
    [InlineData((byte)0x0D, typeof(CarriageReturn))]
    public void C0ControlsEmitTypedEvents(byte b, Type expected)
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        parser.Feed(new[] { b });

        events.Should().HaveCount(1);
        events[0].Should().BeOfType(expected);
    }

    // -- UTF-8 decoding --------------------------------------------------

    [Fact]
    public void TwoByteUtf8DecodesToOneRune()
    {
        var events = ParseUtf8("é");

        events.Should().ContainSingle()
            .Which.Should().BeOfType<PrintRune>()
            .Which.Glyph.Should().Be(new Rune('é'));
    }

    [Fact]
    public void ThreeByteUtf8DecodesToOneRune()
    {
        // Snowman ☃ U+2603, encoded as E2 98 83.
        var events = ParseUtf8("☃");

        events.Should().ContainSingle()
            .Which.Should().BeOfType<PrintRune>()
            .Which.Glyph.Value.Should().Be(0x2603);
    }

    [Fact]
    public void FourByteUtf8DecodesToOneRune()
    {
        // Sparkles emoji ✨ would be 3-byte; use rocket 🚀 U+1F680 for 4-byte.
        var events = ParseUtf8("🚀");

        events.Should().ContainSingle()
            .Which.Should().BeOfType<PrintRune>()
            .Which.Glyph.Value.Should().Be(0x1F680);
    }

    [Fact]
    public void Utf8SplitAcrossFeedCallsStillDecodes()
    {
        var bytes = Encoding.UTF8.GetBytes("☃");
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);

        parser.Feed(bytes.AsSpan(0, 1));
        events.Should().BeEmpty();
        parser.Feed(bytes.AsSpan(1, 1));
        events.Should().BeEmpty();
        parser.Feed(bytes.AsSpan(2, 1));

        events.Should().ContainSingle()
            .Which.Should().BeOfType<PrintRune>()
            .Which.Glyph.Value.Should().Be(0x2603);
    }

    [Fact]
    public void StrayContinuationByteEmitsReplacement()
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);

        parser.Feed(new byte[] { 0x80 });

        events.Should().ContainSingle()
            .Which.Should().BeOfType<PrintRune>()
            .Which.Glyph.Should().Be(Rune.ReplacementChar);
    }

    // -- ESC singletons --------------------------------------------------

    [Fact]
    public void Esc7EmitsSaveCursor()
    {
        ParseAll("\u001B7").Should().Equal(new SaveCursor());
    }

    [Fact]
    public void Esc8EmitsRestoreCursor()
    {
        ParseAll("\u001B8").Should().Equal(new RestoreCursor());
    }

    [Fact]
    public void EscCEmitsResetTerminal()
    {
        ParseAll("\u001Bc").Should().Equal(new ResetTerminal());
    }

    [Fact]
    public void EscMEmitsReverseIndexAsCursorUp()
    {
        ParseAll("\u001BM").Should().Equal(new MoveCursorUp(1));
    }

    [Fact]
    public void EscEEmitsCarriageReturnPlusLineFeed()
    {
        ParseAll("\u001BE").Should().Equal(new CarriageReturn(), new LineFeed());
    }

    [Fact]
    public void EscDEmitsLineFeed()
    {
        ParseAll("\u001BD").Should().Equal(new LineFeed());
    }

    [Fact]
    public void UnknownEscFinalProducesUnknownSequenceAndReturnsToGround()
    {
        var events = ParseAll("\u001BzA");

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<UnknownSequence>();
        events[1].Should().Be(new PrintRune(new Rune('A')));
    }

    // -- CSI cursor motion ----------------------------------------------

    [Theory]
    [InlineData("\u001B[A", typeof(MoveCursorUp), 1)]
    [InlineData("\u001B[3A", typeof(MoveCursorUp), 3)]
    [InlineData("\u001B[B", typeof(MoveCursorDown), 1)]
    [InlineData("\u001B[5B", typeof(MoveCursorDown), 5)]
    [InlineData("\u001B[C", typeof(MoveCursorForward), 1)]
    [InlineData("\u001B[10C", typeof(MoveCursorForward), 10)]
    [InlineData("\u001B[D", typeof(MoveCursorBack), 1)]
    [InlineData("\u001B[2D", typeof(MoveCursorBack), 2)]
    public void CursorMotionDefaultsToOneAndAcceptsExplicitCount(string input, Type type, int expected)
    {
        var events = ParseAll(input);
        events.Should().ContainSingle().Which.Should().BeOfType(type);

        var n = type.GetProperty("Lines") ?? type.GetProperty("Columns");
        n.Should().NotBeNull();
        n!.GetValue(events[0]).Should().Be(expected);
    }

    [Theory]
    [InlineData("\u001B[H", 1, 1)]
    [InlineData("\u001B[5;10H", 5, 10)]
    [InlineData("\u001B[5;10f", 5, 10)]
    [InlineData("\u001B[;10H", 1, 10)]
    [InlineData("\u001B[5;H", 5, 1)]
    public void CupAcceptsAbsoluteRowColumnDefaultingToOne(string input, int row, int col)
    {
        ParseAll(input).Should().Equal(new SetCursorPosition(row, col));
    }

    [Fact]
    public void XtermSaveAndRestoreCursorViaCsiSAndU()
    {
        ParseAll("\u001B[s").Should().Equal(new SaveCursor());
        ParseAll("\u001B[u").Should().Equal(new RestoreCursor());
    }

    // -- Erase -----------------------------------------------------------

    [Theory]
    [InlineData("\u001B[J", EraseMode.ToEnd)]
    [InlineData("\u001B[0J", EraseMode.ToEnd)]
    [InlineData("\u001B[1J", EraseMode.ToStart)]
    [InlineData("\u001B[2J", EraseMode.All)]
    [InlineData("\u001B[3J", EraseMode.Scrollback)]
    public void EraseInDisplayMapsParameter(string input, EraseMode mode)
    {
        ParseAll(input).Should().Equal(new EraseInDisplay(mode));
    }

    [Theory]
    [InlineData("\u001B[K", EraseMode.ToEnd)]
    [InlineData("\u001B[1K", EraseMode.ToStart)]
    [InlineData("\u001B[2K", EraseMode.All)]
    public void EraseInLineMapsParameter(string input, EraseMode mode)
    {
        ParseAll(input).Should().Equal(new EraseInLine(mode));
    }

    [Theory]
    [InlineData("\u001B[3S", typeof(ScrollUp), 3)]
    [InlineData("\u001B[T", typeof(ScrollDown), 1)]
    public void ScrollSequencesDispatch(string input, Type type, int expected)
    {
        var events = ParseAll(input);
        events.Should().ContainSingle().Which.Should().BeOfType(type);
        type.GetProperty("Lines")!.GetValue(events[0]).Should().Be(expected);
    }

    // -- DEC private modes ----------------------------------------------

    [Theory]
    [InlineData("\u001B[?25h", true)]
    [InlineData("\u001B[?25l", false)]
    public void DectcemMapsToCursorVisibility(string input, bool visible)
    {
        ParseAll(input).Should().Equal(new SetCursorVisibility(visible));
    }

    [Theory]
    [InlineData("\u001B[?1049h", true)]
    [InlineData("\u001B[?1049l", false)]
    public void Mode1049MapsToAlternateScreen(string input, bool use)
    {
        ParseAll(input).Should().Equal(new SetUseAlternateScreen(use));
    }

    [Theory]
    [InlineData("\u001B[?2004h", true)]
    [InlineData("\u001B[?2004l", false)]
    public void Mode2004MapsToBracketedPaste(string input, bool enabled)
    {
        ParseAll(input).Should().Equal(new SetBracketedPaste(enabled));
    }

    [Fact]
    public void UnknownDecPrivateModeFallsBackToSetMode()
    {
        ParseAll("\u001B[?1000h").Should().Equal(new SetMode(1000, true, true));
    }

    [Fact]
    public void NonPrivateModeIsTaggedAsNotDecPrivate()
    {
        ParseAll("\u001B[4h").Should().Equal(new SetMode(4, false, true));
    }

    [Fact]
    public void MultipleModesInOneSequenceEmitMultipleEvents()
    {
        ParseAll("\u001B[?25;1049h").Should().Equal(
            new SetCursorVisibility(true),
            new SetUseAlternateScreen(true));
    }

    // -- SGR -------------------------------------------------------------

    [Fact]
    public void EmptySgrEmitsReset()
    {
        var events = ParseAll("\u001B[m");
        events.Should().ContainSingle().Which.Should().BeOfType<SetGraphicsRendition>()
            .Which.Parameters.Should().Equal(new SgrReset());
    }

    [Fact]
    public void SgrZeroEmitsReset()
    {
        var events = ParseAll("\u001B[0m");
        ((SetGraphicsRendition)events[0]).Parameters.Should().Equal(new SgrReset());
    }

    [Fact]
    public void BasicForegroundColoursMapToIndices0Through7()
    {
        var events = ParseAll("\u001B[31m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrForegroundIndex(1));
    }

    [Fact]
    public void BrightBackgroundColoursMapToIndices8Through15()
    {
        var events = ParseAll("\u001B[105m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrBackgroundIndex(13));
    }

    [Fact]
    public void Sgr256ColourForeground()
    {
        var events = ParseAll("\u001B[38;5;208m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrForeground256(208));
    }

    [Fact]
    public void Sgr256ColourBackground()
    {
        var events = ParseAll("\u001B[48;5;15m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrBackground256(15));
    }

    [Fact]
    public void SgrRgbForeground()
    {
        var events = ParseAll("\u001B[38;2;10;20;30m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrForegroundRgb(10, 20, 30));
    }

    [Fact]
    public void SgrRgbBackground()
    {
        var events = ParseAll("\u001B[48;2;255;0;128m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrBackgroundRgb(255, 0, 128));
    }

    [Fact]
    public void SgrCompoundEmitsParametersInOrder()
    {
        var events = ParseAll("\u001B[1;31;48;5;236m");
        ((SetGraphicsRendition)events[0]).Parameters.Should().Equal(
            new SgrBold(true),
            new SgrForegroundIndex(1),
            new SgrBackground256(236));
    }

    [Theory]
    [InlineData(1, typeof(SgrBold), true)]
    [InlineData(22, typeof(SgrBold), false)]
    [InlineData(2, typeof(SgrDim), true)]
    [InlineData(3, typeof(SgrItalic), true)]
    [InlineData(23, typeof(SgrItalic), false)]
    [InlineData(4, typeof(SgrUnderline), true)]
    [InlineData(24, typeof(SgrUnderline), false)]
    [InlineData(7, typeof(SgrInverse), true)]
    [InlineData(27, typeof(SgrInverse), false)]
    [InlineData(9, typeof(SgrStrikethrough), true)]
    [InlineData(29, typeof(SgrStrikethrough), false)]
    public void SimpleSgrAttributesMap(int param, Type type, bool on)
    {
        var events = ParseAll($"\u001B[{param}m");
        var p = ((SetGraphicsRendition)events[0]).Parameters.Single();
        p.Should().BeOfType(type);
        type.GetProperty("On")!.GetValue(p).Should().Be(on);
    }

    [Fact]
    public void DefaultForegroundAndBackground()
    {
        var events = ParseAll("\u001B[39;49m");
        ((SetGraphicsRendition)events[0]).Parameters.Should().Equal(
            new SgrForegroundDefault(),
            new SgrBackgroundDefault());
    }

    [Fact]
    public void UnknownSgrParameterSurfacesAsSgrUnknown()
    {
        var events = ParseAll("\u001B[99m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrUnknown(99));
    }

    [Fact]
    public void Malformed38ExtendedColourEmitsSgrUnknownAndContinues()
    {
        // 38 with no follow-up params; the next param (1) should still parse.
        var events = ParseAll("\u001B[38m");
        ((SetGraphicsRendition)events[0]).Parameters
            .Should().Equal(new SgrUnknown(38));
    }

    // -- OSC -------------------------------------------------------------

    [Fact]
    public void Osc0BellSetsWindowTitle()
    {
        ParseAll("\u001B]0;Hello\u0007").Should().Equal(new SetWindowTitle("Hello"));
    }

    [Fact]
    public void Osc2StSetsWindowTitle()
    {
        ParseAll("\u001B]2;World\u001B\\").Should().Equal(new SetWindowTitle("World"));
    }

    [Fact]
    public void OscWithUnknownCommandIsDiagnostic()
    {
        var events = ParseAll("\u001B]52;clipboard\u0007");
        events.Should().ContainSingle().Which.Should().BeOfType<UnknownSequence>()
            .Which.Description.Should().Be("OSC 52");
    }

    [Fact]
    public void OscFollowedByPrintableContinuesParsing()
    {
        var events = ParseAll("\u001B]0;t\u0007X");
        events.Should().Equal(new SetWindowTitle("t"), new PrintRune(new Rune('X')));
    }

    // -- Split / interruption / partial sequences -----------------------

    [Fact]
    public void CsiSplitAcrossManyFeedCallsStillParses()
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);

        parser.Feed(new byte[] { 0x1B });
        parser.Feed(new byte[] { (byte)'[' });
        parser.Feed(new byte[] { (byte)'1' });
        parser.Feed(new byte[] { (byte)';' });
        parser.Feed(new byte[] { (byte)'2' });
        parser.Feed(new byte[] { (byte)'H' });

        events.Should().Equal(new SetCursorPosition(1, 2));
    }

    [Fact]
    public void CanByteAbortsInFlightSequence()
    {
        // ESC [ 1 ; CAN  X — CAN cancels, X prints.
        var events = ParseAll("\u001B[1;\u0018X");
        events.Should().Equal(new PrintRune(new Rune('X')));
    }

    [Fact]
    public void EscWithinSequenceRestartsTheSequence()
    {
        // ESC [ 1 ESC [ 2 H — first ESC[1 is abandoned, second runs to "go to 2;1".
        var events = ParseAll("\u001B[1\u001B[2H");
        events.Should().Equal(new SetCursorPosition(2, 1));
    }

    [Fact]
    public void PrintableSurroundedByCsiEmitsInOrder()
    {
        var events = ParseAll("X\u001B[31mY\u001B[0mZ");

        events.Should().HaveCount(5);
        events[0].Should().Be(new PrintRune(new Rune('X')));
        events[1].Should().BeOfType<SetGraphicsRendition>();
        events[2].Should().Be(new PrintRune(new Rune('Y')));
        events[3].Should().BeOfType<SetGraphicsRendition>();
        events[4].Should().Be(new PrintRune(new Rune('Z')));
    }

    // -- Reset / lifecycle ----------------------------------------------

    [Fact]
    public void ResetReturnsParserToGroundAndDiscardsPending()
    {
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);

        parser.Feed(new byte[] { 0x1B, (byte)'[', (byte)'3' });
        parser.Reset();
        parser.Feed(new byte[] { (byte)'A' });

        events.Should().Equal(new PrintRune(new Rune('A')));
    }

    [Fact]
    public void ConstructorRejectsNullEmitter()
    {
        Action a = () => _ = new VtParser(null!);
        a.Should().Throw<ArgumentNullException>();
    }

    // -- Unknown CSI surfaces diagnostic --------------------------------

    [Fact]
    public void UnknownCsiFinalProducesUnknownSequence()
    {
        var events = ParseAll("\u001B[2~");
        events.Should().ContainSingle().Which.Should().BeOfType<UnknownSequence>()
            .Which.Description.Should().StartWith("CSI ");
    }

    [Fact]
    public void CsiIntermediateBytesProduceUnknownSequence()
    {
        // CSI 1 SP @ — uses an intermediate byte; we don't model it.
        var events = ParseAll("\u001B[1 @");
        events.Should().ContainSingle().Which.Should().BeOfType<UnknownSequence>();
    }

    // -- A representative multi-event prompt redraw ---------------------

    [Fact]
    public void RealisticPromptRedrawParsesEachEvent()
    {
        // Clear screen, home cursor, set fg cyan, print "Copilot> ", reset.
        var events = ParseAll("\u001B[2J\u001B[H\u001B[36mCopilot> \u001B[0m");

        var kinds = events.Select(e => e.GetType()).ToArray();
        kinds.Should().StartWith(new[]
        {
            typeof(EraseInDisplay),
            typeof(SetCursorPosition),
            typeof(SetGraphicsRendition),
        });
        kinds.Last().Should().Be(typeof(SetGraphicsRendition));
        events.Count(e => e is PrintRune).Should().Be(9); // "Copilot> "
    }
}
