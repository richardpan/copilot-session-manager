using System.Collections.Generic;
using System.Text;
using System.Windows.Input;
using CopilotSessionManager.Terminal;
using FluentAssertions;
using Xunit;

namespace CopilotSessionManager.Terminal.Wpf.Tests;

public class TerminalControlKeyboardTests
{
    [Fact]
    public void Cursor_key_emits_normal_mode_csi_through_InputProduced() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInput(control);

        control.DispatchKeyForTest(Key.Right, ModifierKeys.None)
            .Should().BeTrue("Right Arrow should be encoded");

        captured.Single().Should().Be("\u001B[C");
    });

    [Fact]
    public void Application_cursor_keys_emit_ss3_when_property_enabled() => StaRunner.Run(() =>
    {
        var control = NewControl();
        control.UseApplicationCursorKeys = true;
        var captured = HookInput(control);

        control.DispatchKeyForTest(Key.Up, ModifierKeys.None).Should().BeTrue();

        captured.Single().Should().Be("\u001BOA");
    });

    [Fact]
    public void Ctrl_plus_arrow_emits_modifier_csi() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInput(control);

        control.DispatchKeyForTest(Key.Right, ModifierKeys.Control).Should().BeTrue();

        captured.Single().Should().Be("\u001B[1;5C");
    });

    [Fact]
    public void Function_keys_use_ss3_through_F4_and_tilde_above() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInput(control);

        control.DispatchKeyForTest(Key.F2, ModifierKeys.None).Should().BeTrue();
        control.DispatchKeyForTest(Key.F5, ModifierKeys.None).Should().BeTrue();
        control.DispatchKeyForTest(Key.F12, ModifierKeys.None).Should().BeTrue();

        captured.Should().Equal("\u001BOQ", "\u001B[15~", "\u001B[24~");
    });

    [Fact]
    public void Enter_tab_and_backspace_emit_c0_bytes() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInputBytes(control);

        control.DispatchKeyForTest(Key.Enter, ModifierKeys.None).Should().BeTrue();
        control.DispatchKeyForTest(Key.Tab, ModifierKeys.None).Should().BeTrue();
        control.DispatchKeyForTest(Key.Back, ModifierKeys.None).Should().BeTrue();
        control.DispatchKeyForTest(Key.Escape, ModifierKeys.None).Should().BeTrue();

        captured.Should().HaveCount(4);
        captured[0].Should().Equal(new byte[] { 0x0D });
        captured[1].Should().Equal(new byte[] { 0x09 });
        captured[2].Should().Equal(new byte[] { 0x7F });
        captured[3].Should().Equal(new byte[] { 0x1B });
    });

    [Fact]
    public void Shift_tab_emits_csi_Z() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInput(control);

        control.DispatchKeyForTest(Key.Tab, ModifierKeys.Shift).Should().BeTrue();

        captured.Single().Should().Be("\u001B[Z");
    });

    [Theory]
    [InlineData(Key.A, 0x01)]
    [InlineData(Key.M, 0x0D)]
    [InlineData(Key.Z, 0x1A)]
    [InlineData(Key.Space, 0x00)]
    public void Ctrl_plus_letter_emits_c0_control_byte(Key key, byte expected)
    {
        StaRunner.Run(() =>
        {
            var control = NewControl();
            var captured = HookInputBytes(control);

            control.DispatchKeyForTest(key, ModifierKeys.Control).Should().BeTrue();

            captured.Single().Should().Equal(new[] { expected });
        });
    }

    [Fact]
    public void Alt_plus_letter_prefixes_escape() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInputBytes(control);

        control.DispatchKeyForTest(Key.A, ModifierKeys.Alt).Should().BeTrue();

        captured.Single().Should().Equal(new byte[] { 0x1B, (byte)'a' });
    });

    [Fact]
    public void Ctrl_alt_letter_prefixes_escape_to_control_byte() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInputBytes(control);

        control.DispatchKeyForTest(Key.C, ModifierKeys.Control | ModifierKeys.Alt)
            .Should().BeTrue();

        captured.Single().Should().Equal(new byte[] { 0x1B, 0x03 });
    });

    [Fact]
    public void TextInput_emits_utf8_bytes() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInputBytes(control);

        control.DispatchTextInputForTest("héllo", ModifierKeys.None).Should().BeTrue();

        captured.Single().Should().Equal(Encoding.UTF8.GetBytes("héllo"));
    });

    [Fact]
    public void TextInput_after_handled_special_key_is_suppressed_once() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInputBytes(control);

        control.DispatchKeyForTest(Key.Enter, ModifierKeys.None).Should().BeTrue();
        // WPF would now deliver TextInput="\r"; we expect that to be swallowed.
        control.DispatchTextInputForTest("\r", ModifierKeys.None).Should().BeTrue();
        // A subsequent unrelated text input must still pass through.
        control.DispatchTextInputForTest("x", ModifierKeys.None).Should().BeTrue();

        captured.Should().HaveCount(2);
        captured[0].Should().Equal(new byte[] { 0x0D });
        captured[1].Should().Equal(new byte[] { (byte)'x' });
    });

    [Fact]
    public void Paste_emits_raw_bytes_when_bracketed_paste_disabled() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 12);
        // BracketedPasteEnabled is false by default.
        control.Buffer = buffer;
        var captured = HookInput(control);

        control.Paste("hi");

        captured.Single().Should().Be("hi");
    });

    [Fact]
    public void Paste_wraps_with_dec_2004_delimiters_when_buffer_has_bracketed_paste() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var buffer = new ScreenBuffer(rows: 4, columns: 12);
        var events = new List<VtEvent>();
        var parser = new VtParser(events.Add);
        // ESC [ ? 2004 h enables bracketed paste.
        parser.Feed(Encoding.ASCII.GetBytes("\u001B[?2004h"));
        buffer.ApplyAll(events);
        buffer.BracketedPasteEnabled.Should().BeTrue();

        control.Buffer = buffer;
        var captured = HookInput(control);

        control.Paste("hello");

        captured.Single().Should().Be("\u001B[200~hello\u001B[201~");
    });

    [Fact]
    public void Paste_with_no_buffer_treats_bracketed_paste_as_disabled() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInput(control);

        control.Paste("hi");

        captured.Single().Should().Be("hi");
    });

    [Fact]
    public void Unmapped_key_with_no_modifiers_returns_false_without_emit() => StaRunner.Run(() =>
    {
        var control = NewControl();
        var captured = HookInput(control);

        control.DispatchKeyForTest(Key.Scroll, ModifierKeys.None).Should().BeFalse();
        captured.Should().BeEmpty();
    });

    [Fact]
    public void Control_is_focusable() => StaRunner.Run(() =>
    {
        var control = NewControl();
        control.Focusable.Should().BeTrue("the terminal must accept keyboard focus to receive key events");
    });

    private static TerminalControl NewControl() => new()
    {
        FontSize = 14.0,
    };

    private static List<string> HookInput(TerminalControl control)
    {
        var captured = new List<string>();
        control.InputProduced += (_, e) =>
            captured.Add(Encoding.UTF8.GetString(e.Bytes.Span));
        return captured;
    }

    private static List<byte[]> HookInputBytes(TerminalControl control)
    {
        var captured = new List<byte[]>();
        control.InputProduced += (_, e) => captured.Add(e.Bytes.ToArray());
        return captured;
    }
}
