using System.Text;
using CopilotSessionManager.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Terminal.Tests;

public class VtKeyEncoderTests
{
    [Theory]
    [InlineData(TerminalKey.Up, "\u001B[A")]
    [InlineData(TerminalKey.Down, "\u001B[B")]
    [InlineData(TerminalKey.Right, "\u001B[C")]
    [InlineData(TerminalKey.Left, "\u001B[D")]
    [InlineData(TerminalKey.Home, "\u001B[H")]
    [InlineData(TerminalKey.End, "\u001B[F")]
    public void Normal_mode_cursor_keys_use_csi(TerminalKey key, string expected)
    {
        var bytes = VtKeyEncoder.Encode(key);
        ToAscii(bytes).Should().Be(expected);
    }

    [Theory]
    [InlineData(TerminalKey.Up, "\u001BOA")]
    [InlineData(TerminalKey.Down, "\u001BOB")]
    [InlineData(TerminalKey.Right, "\u001BOC")]
    [InlineData(TerminalKey.Left, "\u001BOD")]
    [InlineData(TerminalKey.Home, "\u001BOH")]
    [InlineData(TerminalKey.End, "\u001BOF")]
    public void Application_mode_cursor_keys_use_ss3(TerminalKey key, string expected)
    {
        var bytes = VtKeyEncoder.Encode(key, applicationCursorKeys: true);
        ToAscii(bytes).Should().Be(expected);
    }

    [Theory]
    [InlineData(TerminalKeyModifiers.Shift, '2')]
    [InlineData(TerminalKeyModifiers.Alt, '3')]
    [InlineData(TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt, '4')]
    [InlineData(TerminalKeyModifiers.Control, '5')]
    [InlineData(TerminalKeyModifiers.Shift | TerminalKeyModifiers.Control, '6')]
    [InlineData(TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control, '7')]
    [InlineData(
        TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control,
        '8')]
    public void Modified_cursor_keys_use_xterm_modifier_parameter(
        TerminalKeyModifiers modifiers,
        char expectedModDigit)
    {
        var bytes = VtKeyEncoder.Encode(TerminalKey.Up, modifiers);
        ToAscii(bytes).Should().Be($"\u001B[1;{expectedModDigit}A");
    }

    [Fact]
    public void Modified_cursor_keys_ignore_application_mode()
    {
        // With any modifier present, xterm always emits the CSI form even
        // when DECCKM is on.
        var bytes = VtKeyEncoder.Encode(
            TerminalKey.Right,
            TerminalKeyModifiers.Control,
            applicationCursorKeys: true);
        ToAscii(bytes).Should().Be("\u001B[1;5C");
    }

    [Theory]
    [InlineData(TerminalKey.Insert, "\u001B[2~")]
    [InlineData(TerminalKey.Delete, "\u001B[3~")]
    [InlineData(TerminalKey.PageUp, "\u001B[5~")]
    [InlineData(TerminalKey.PageDown, "\u001B[6~")]
    public void Editing_keys_use_tilde_form(TerminalKey key, string expected)
    {
        ToAscii(VtKeyEncoder.Encode(key)).Should().Be(expected);
    }

    [Fact]
    public void Modified_editing_keys_inject_modifier_parameter()
    {
        ToAscii(VtKeyEncoder.Encode(TerminalKey.PageUp, TerminalKeyModifiers.Control))
            .Should().Be("\u001B[5;5~");
        ToAscii(VtKeyEncoder.Encode(TerminalKey.Delete, TerminalKeyModifiers.Shift))
            .Should().Be("\u001B[3;2~");
    }

    [Theory]
    [InlineData(TerminalKey.F1, "\u001BOP")]
    [InlineData(TerminalKey.F2, "\u001BOQ")]
    [InlineData(TerminalKey.F3, "\u001BOR")]
    [InlineData(TerminalKey.F4, "\u001BOS")]
    public void F1_through_F4_use_ss3(TerminalKey key, string expected)
    {
        ToAscii(VtKeyEncoder.Encode(key)).Should().Be(expected);
    }

    [Theory]
    [InlineData(TerminalKey.F5, "\u001B[15~")]
    [InlineData(TerminalKey.F6, "\u001B[17~")]
    [InlineData(TerminalKey.F7, "\u001B[18~")]
    [InlineData(TerminalKey.F8, "\u001B[19~")]
    [InlineData(TerminalKey.F9, "\u001B[20~")]
    [InlineData(TerminalKey.F10, "\u001B[21~")]
    [InlineData(TerminalKey.F11, "\u001B[23~")]
    [InlineData(TerminalKey.F12, "\u001B[24~")]
    public void F5_and_above_use_tilde_form(TerminalKey key, string expected)
    {
        ToAscii(VtKeyEncoder.Encode(key)).Should().Be(expected);
    }

    [Fact]
    public void Modified_F1_through_F4_use_csi_form()
    {
        ToAscii(VtKeyEncoder.Encode(TerminalKey.F1, TerminalKeyModifiers.Shift))
            .Should().Be("\u001B[1;2P");
    }

    [Fact]
    public void Tab_returns_horizontal_tab_byte_unless_shifted()
    {
        VtKeyEncoder.Encode(TerminalKey.Tab).Should().Equal(new byte[] { 0x09 });
        ToAscii(VtKeyEncoder.Encode(TerminalKey.Tab, TerminalKeyModifiers.Shift))
            .Should().Be("\u001B[Z");
    }

    [Fact]
    public void Enter_returns_carriage_return()
    {
        VtKeyEncoder.Encode(TerminalKey.Enter).Should().Equal(new byte[] { 0x0D });
    }

    [Fact]
    public void Backspace_returns_DEL_normally_and_BS_under_control()
    {
        VtKeyEncoder.Encode(TerminalKey.Backspace).Should().Equal(new byte[] { 0x7F });
        VtKeyEncoder.Encode(TerminalKey.Backspace, TerminalKeyModifiers.Control)
            .Should().Equal(new byte[] { 0x08 });
    }

    [Fact]
    public void Escape_returns_escape_byte()
    {
        VtKeyEncoder.Encode(TerminalKey.Escape).Should().Equal(new byte[] { 0x1B });
    }

    [Fact]
    public void None_returns_null()
    {
        VtKeyEncoder.Encode(TerminalKey.None).Should().BeNull();
    }

    [Theory]
    [InlineData('a', 0x01)]
    [InlineData('A', 0x01)]
    [InlineData('z', 0x1A)]
    [InlineData('Z', 0x1A)]
    [InlineData(' ', 0x00)]
    [InlineData('@', 0x00)]
    [InlineData('[', 0x1B)]
    [InlineData('\\', 0x1C)]
    [InlineData(']', 0x1D)]
    [InlineData('^', 0x1E)]
    [InlineData('_', 0x1F)]
    [InlineData('?', 0x7F)]
    public void EncodeControlChar_maps_to_c0_byte(char ch, byte expected)
    {
        VtKeyEncoder.EncodeControlChar(ch).Should().Equal(new[] { expected });
    }

    [Fact]
    public void EncodeControlChar_returns_null_for_unmapped_char()
    {
        VtKeyEncoder.EncodeControlChar('1').Should().BeNull();
    }

    [Fact]
    public void EncodeText_returns_utf8_bytes_by_default()
    {
        var bytes = VtKeyEncoder.EncodeText("héllo");
        bytes.Should().Equal(Encoding.UTF8.GetBytes("héllo"));
    }

    [Fact]
    public void EncodeText_with_alt_held_prefixes_escape()
    {
        var bytes = VtKeyEncoder.EncodeText("a", altHeld: true);
        bytes.Should().Equal(new byte[] { 0x1B, (byte)'a' });
    }

    [Fact]
    public void EncodeText_with_empty_string_returns_empty()
    {
        VtKeyEncoder.EncodeText(string.Empty).Should().BeEmpty();
        VtKeyEncoder.EncodeText(string.Empty, altHeld: true).Should().BeEmpty();
    }

    [Fact]
    public void EncodePaste_passes_through_when_bracketed_paste_disabled()
    {
        var bytes = VtKeyEncoder.EncodePaste("hi", bracketedPasteEnabled: false);
        bytes.Should().Equal(Encoding.UTF8.GetBytes("hi"));
    }

    [Fact]
    public void EncodePaste_wraps_with_dec_2004_delimiters_when_enabled()
    {
        var bytes = VtKeyEncoder.EncodePaste("hi", bracketedPasteEnabled: true);
        ToAscii(bytes).Should().Be("\u001B[200~hi\u001B[201~");
    }

    [Fact]
    public void EncodePaste_with_empty_text_emits_only_delimiters()
    {
        var bytes = VtKeyEncoder.EncodePaste(string.Empty, bracketedPasteEnabled: true);
        ToAscii(bytes).Should().Be("\u001B[200~\u001B[201~");
    }

    private static string ToAscii(byte[]? bytes)
    {
        bytes.Should().NotBeNull();
        return Encoding.ASCII.GetString(bytes!);
    }
}
