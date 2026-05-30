using System;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Encodes <see cref="TerminalKey"/> presses and printable characters into
/// the byte sequences a Unix-style application reads from its terminal.
/// Pure: no UI dependencies, no allocations beyond the returned array.
/// </summary>
/// <remarks>
/// Sequences follow xterm conventions as documented in
/// <see href="https://invisible-island.net/xterm/ctlseqs/ctlseqs.html"/>.
/// Modifier-aware CSI sequences use the parameter <c>1 + Shift + 2*Alt +
/// 4*Ctrl</c> in the canonical xterm form (e.g. <c>ESC [ 1 ; 5 A</c> for
/// Ctrl+Up).
/// </remarks>
public static class VtKeyEncoder
{
    private const byte Esc = 0x1B;

    /// <summary>
    /// Encode a special key press into the byte sequence that should be
    /// written to the PTY input stream. Returns <c>null</c> when the
    /// encoder declines to handle the key (e.g. <see cref="TerminalKey.None"/>).
    /// </summary>
    /// <param name="key">The logical key pressed.</param>
    /// <param name="modifiers">Held modifier keys.</param>
    /// <param name="applicationCursorKeys">
    /// <c>true</c> when the host application has enabled DECCKM (cursor
    /// key application mode). Affects the cursor keys, Home, and End.
    /// </param>
    public static byte[]? Encode(
        TerminalKey key,
        TerminalKeyModifiers modifiers = TerminalKeyModifiers.None,
        bool applicationCursorKeys = false)
    {
        switch (key)
        {
            case TerminalKey.None:
                return null;

            case TerminalKey.Tab:
                return modifiers == TerminalKeyModifiers.Shift
                    ? new byte[] { Esc, (byte)'[', (byte)'Z' }
                    : new byte[] { 0x09 };

            case TerminalKey.Enter:
                // Plain Enter → CR (submit). Shift+Enter → LF (insert newline
                // in multiline input). This matches the convention used by
                // the Copilot CLI, Claude Code, and most readline-based TUI
                // apps. Without it, Shift+Enter would be indistinguishable
                // from Enter and always submit, breaking multiline input.
                return (modifiers & TerminalKeyModifiers.Shift) != 0
                    ? new byte[] { 0x0A }
                    : new byte[] { 0x0D };

            case TerminalKey.Backspace:
                // Ctrl+Backspace is conventionally word-delete (BS, 0x08)
                // in many shells; fall back to the same 0x7F otherwise so
                // line-editing in bash/zsh/PowerShell behaves as expected.
                return (modifiers & TerminalKeyModifiers.Control) != 0
                    ? new byte[] { 0x08 }
                    : new byte[] { 0x7F };

            case TerminalKey.Escape:
                return new byte[] { Esc };

            case TerminalKey.Up:
                return CursorKey('A', modifiers, applicationCursorKeys);
            case TerminalKey.Down:
                return CursorKey('B', modifiers, applicationCursorKeys);
            case TerminalKey.Right:
                return CursorKey('C', modifiers, applicationCursorKeys);
            case TerminalKey.Left:
                return CursorKey('D', modifiers, applicationCursorKeys);
            case TerminalKey.Home:
                return CursorKey('H', modifiers, applicationCursorKeys);
            case TerminalKey.End:
                return CursorKey('F', modifiers, applicationCursorKeys);

            case TerminalKey.Insert:
                return TildeKey(2, modifiers);
            case TerminalKey.Delete:
                return TildeKey(3, modifiers);
            case TerminalKey.PageUp:
                return TildeKey(5, modifiers);
            case TerminalKey.PageDown:
                return TildeKey(6, modifiers);

            case TerminalKey.F1:
                return FunctionKey1To4('P', modifiers);
            case TerminalKey.F2:
                return FunctionKey1To4('Q', modifiers);
            case TerminalKey.F3:
                return FunctionKey1To4('R', modifiers);
            case TerminalKey.F4:
                return FunctionKey1To4('S', modifiers);

            case TerminalKey.F5:
                return TildeKey(15, modifiers);
            case TerminalKey.F6:
                return TildeKey(17, modifiers);
            case TerminalKey.F7:
                return TildeKey(18, modifiers);
            case TerminalKey.F8:
                return TildeKey(19, modifiers);
            case TerminalKey.F9:
                return TildeKey(20, modifiers);
            case TerminalKey.F10:
                return TildeKey(21, modifiers);
            case TerminalKey.F11:
                return TildeKey(23, modifiers);
            case TerminalKey.F12:
                return TildeKey(24, modifiers);

            default:
                return null;
        }
    }

    /// <summary>
    /// Encode a Ctrl+character chord into the matching C0 control byte.
    /// Returns <c>null</c> if <paramref name="ch"/> has no canonical
    /// control mapping (caller should fall through to printable encoding).
    /// </summary>
    public static byte[]? EncodeControlChar(char ch)
    {
        if (ch >= 'a' && ch <= 'z')
        {
            return new[] { (byte)(ch - 'a' + 1) };
        }
        if (ch >= 'A' && ch <= 'Z')
        {
            return new[] { (byte)(ch - 'A' + 1) };
        }

        return ch switch
        {
            ' ' => new byte[] { 0x00 },
            '@' => new byte[] { 0x00 },
            '[' => new byte[] { 0x1B },
            '\\' => new byte[] { 0x1C },
            ']' => new byte[] { 0x1D },
            '^' => new byte[] { 0x1E },
            '_' => new byte[] { 0x1F },
            '?' => new byte[] { 0x7F },
            _ => null,
        };
    }

    /// <summary>
    /// Encode a printable text run (typically from an OS text-composition
    /// event) as UTF-8. If <paramref name="altHeld"/> is <c>true</c>, the
    /// result is prefixed with <c>ESC</c> to convey the Meta modifier.
    /// </summary>
    public static byte[] EncodeText(string text, bool altHeld = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<byte>();
        }

        var body = Encoding.UTF8.GetBytes(text);
        if (!altHeld)
        {
            return body;
        }

        var combined = new byte[body.Length + 1];
        combined[0] = Esc;
        Array.Copy(body, 0, combined, 1, body.Length);
        return combined;
    }

    /// <summary>
    /// Wrap <paramref name="text"/> with bracketed-paste delimiters when
    /// <paramref name="bracketedPasteEnabled"/> is <c>true</c>, otherwise
    /// emit the UTF-8 bytes unchanged. Embedded ESC bytes in the payload
    /// are left intact; sanitising them is the host's responsibility.
    /// </summary>
    public static byte[] EncodePaste(string text, bool bracketedPasteEnabled)
    {
        var body = Encoding.UTF8.GetBytes(text ?? string.Empty);
        if (!bracketedPasteEnabled)
        {
            return body;
        }

        // ESC [ 200 ~ <body> ESC [ 201 ~
        var start = new byte[] { Esc, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~' };
        var end = new byte[] { Esc, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~' };
        var result = new byte[start.Length + body.Length + end.Length];
        Buffer.BlockCopy(start, 0, result, 0, start.Length);
        Buffer.BlockCopy(body, 0, result, start.Length, body.Length);
        Buffer.BlockCopy(end, 0, result, start.Length + body.Length, end.Length);
        return result;
    }

    private static byte[] CursorKey(char letter, TerminalKeyModifiers modifiers, bool appMode)
    {
        if (modifiers != TerminalKeyModifiers.None)
        {
            // Modifier form always uses CSI and the xterm 1;<mod> prefix,
            // regardless of DECCKM.
            return ModifiedCsi('1', letter, modifiers);
        }

        return appMode
            ? new byte[] { Esc, (byte)'O', (byte)letter }
            : new byte[] { Esc, (byte)'[', (byte)letter };
    }

    private static byte[] TildeKey(int code, TerminalKeyModifiers modifiers)
    {
        var codeDigits = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (modifiers == TerminalKeyModifiers.None)
        {
            // ESC [ <code> ~
            var bytes = new byte[2 + codeDigits.Length + 1];
            bytes[0] = Esc;
            bytes[1] = (byte)'[';
            for (var i = 0; i < codeDigits.Length; i++)
            {
                bytes[2 + i] = (byte)codeDigits[i];
            }
            bytes[^1] = (byte)'~';
            return bytes;
        }

        // ESC [ <code> ; <mod> ~
        var modDigit = (char)('0' + 1 + (int)modifiers);
        var modified = new byte[2 + codeDigits.Length + 1 + 1 + 1];
        modified[0] = Esc;
        modified[1] = (byte)'[';
        var idx = 2;
        for (var i = 0; i < codeDigits.Length; i++)
        {
            modified[idx++] = (byte)codeDigits[i];
        }
        modified[idx++] = (byte)';';
        modified[idx++] = (byte)modDigit;
        modified[idx] = (byte)'~';
        return modified;
    }

    private static byte[] FunctionKey1To4(char letter, TerminalKeyModifiers modifiers)
    {
        if (modifiers == TerminalKeyModifiers.None)
        {
            // SS3 form: ESC O <letter>
            return new byte[] { Esc, (byte)'O', (byte)letter };
        }

        // Modifier form: ESC [ 1 ; <mod> <letter>
        return ModifiedCsi('1', letter, modifiers);
    }

    private static byte[] ModifiedCsi(char paramDigit, char finalByte, TerminalKeyModifiers modifiers)
    {
        var modDigit = (char)('0' + 1 + (int)modifiers);
        return new byte[]
        {
            Esc,
            (byte)'[',
            (byte)paramDigit,
            (byte)';',
            (byte)modDigit,
            (byte)finalByte,
        };
    }
}
