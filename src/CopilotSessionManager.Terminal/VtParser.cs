using System;
using System.Collections.Generic;
using System.Text;

namespace CopilotSessionManager.Terminal;

/// <summary>
/// Hand-rolled minimal VT escape-sequence parser. Models the Paul Williams
/// DEC ANSI parser state machine
/// (<see href="https://vt100.net/emu/dec_ansi_parser"/>) reduced to the
/// subset Copilot CLI emits. Phase 2A of epic #93; design recorded in
/// ADR-0006.
/// </summary>
/// <remarks>
/// <para>
/// The parser is byte-oriented and stateful: bytes are fed in via
/// <see cref="Feed"/> and may arrive in arbitrary chunks (a single CSI
/// sequence can be split across many reads from the ConPTY output pipe).
/// State persists between calls.
/// </para>
/// <para>
/// Recognised events are dispatched synchronously to the
/// <c>emit</c> callback supplied at construction. Unrecognised sequences
/// surface as <see cref="UnknownSequence"/> so they can be triaged from
/// captured traces (Phase 2C) rather than being silently dropped.
/// </para>
/// <para>
/// Thread-safety: instances are not safe for concurrent <see cref="Feed"/>
/// calls. The intended caller is a single dedicated reader task.
/// </para>
/// </remarks>
public sealed class VtParser : IVtParser
{
    private const int MaxParams = 16;
    private const int MaxIntermediates = 2;
    private const int MaxOscLength = 4096;
    private const int MaxRawSequenceLength = 64;

    private readonly Action<VtEvent> _emit;

    private State _state;

    // CSI accumulators
    private readonly int[] _params = new int[MaxParams];
    private int _paramCount;
    private bool _currentParamHasDigits;
    private readonly byte[] _intermediates = new byte[MaxIntermediates];
    private int _intermediateCount;
    private bool _decPrivate;

    // OSC accumulator
    private readonly List<byte> _osc = new(64);

    // Raw bytes of the in-flight sequence (used for diagnostic events).
    private readonly List<byte> _raw = new(16);

    // UTF-8 decoder for printable bytes >= 0x80.
    private readonly byte[] _utf8Buffer = new byte[4];
    private int _utf8Length;
    private int _utf8Expected;

    /// <summary>
    /// Create a parser that dispatches events to <paramref name="emit"/>.
    /// </summary>
    public VtParser(Action<VtEvent> emit)
    {
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _state = State.Ground;
    }

    /// <inheritdoc />
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            Step(bytes[i]);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        _state = State.Ground;
        _paramCount = 0;
        _currentParamHasDigits = false;
        _intermediateCount = 0;
        _decPrivate = false;
        _osc.Clear();
        _raw.Clear();
        _utf8Length = 0;
        _utf8Expected = 0;
    }

    // -- state machine ----------------------------------------------------

    private void Step(byte b)
    {
        // String states (OSC) handle ESC specially as the lead of a
        // String Terminator (ESC \). Process them before the "anywhere"
        // transitions so we don't abort a legitimately terminating OSC.
        if (_state == State.OscString)
        {
            HandleOscString(b);
            return;
        }
        if (_state == State.OscEsc)
        {
            HandleOscEsc(b);
            return;
        }

        // "Anywhere" transitions, per the Williams diagram.
        switch (b)
        {
            case 0x18: // CAN — abort current sequence
            case 0x1A: // SUB — same; treated as cancel
                EnterGround();
                return;
            case 0x1B: // ESC — restart escape flow
                FlushPendingUtf8();
                EnterEscape();
                return;
        }

        switch (_state)
        {
            case State.Ground:
                HandleGround(b);
                break;
            case State.Escape:
                HandleEscape(b);
                break;
            case State.EscapeIntermediate:
                HandleEscapeIntermediate(b);
                break;
            case State.CsiEntry:
                HandleCsiEntry(b);
                break;
            case State.CsiParam:
                HandleCsiParam(b);
                break;
            case State.CsiIntermediate:
                HandleCsiIntermediate(b);
                break;
            case State.CsiIgnore:
                HandleCsiIgnore(b);
                break;
        }
    }

    // -- GROUND -----------------------------------------------------------

    private void HandleGround(byte b)
    {
        if (b < 0x20)
        {
            HandleC0(b);
            return;
        }

        if (b == 0x7F)
        {
            // DEL — ignore in ground (xterm behaviour).
            return;
        }

        if (b < 0x80)
        {
            FlushPendingUtf8();
            _emit(new PrintRune(new Rune(b)));
            return;
        }

        // 0x80+ — UTF-8 lead or continuation.
        AccumulateUtf8(b);
    }

    private void HandleC0(byte b)
    {
        FlushPendingUtf8();
        switch (b)
        {
            case 0x07:
                _emit(new RingBell());
                break;
            case 0x08:
                _emit(new Backspace());
                break;
            case 0x09:
                _emit(new HorizontalTab());
                break;
            case 0x0A: // LF
            case 0x0B: // VT
            case 0x0C: // FF
                _emit(new LineFeed());
                break;
            case 0x0D:
                _emit(new CarriageReturn());
                break;
            default:
                // NUL and other rare C0s: silently ignore.
                break;
        }
    }

    private void AccumulateUtf8(byte b)
    {
        if (_utf8Expected == 0)
        {
            // Lead byte.
            if (b >= 0xC2 && b <= 0xDF)
                _utf8Expected = 2;
            else if (b >= 0xE0 && b <= 0xEF)
                _utf8Expected = 3;
            else if (b >= 0xF0 && b <= 0xF4)
                _utf8Expected = 4;
            else
            {
                // Stray continuation or invalid lead — emit replacement.
                _emit(new PrintRune(Rune.ReplacementChar));
                return;
            }
            _utf8Length = 0;
        }

        _utf8Buffer[_utf8Length++] = b;

        if (_utf8Length < _utf8Expected)
        {
            return;
        }

        var span = new ReadOnlySpan<byte>(_utf8Buffer, 0, _utf8Length);
        if (Rune.DecodeFromUtf8(span, out var rune, out _) == System.Buffers.OperationStatus.Done)
        {
            _emit(new PrintRune(rune));
        }
        else
        {
            _emit(new PrintRune(Rune.ReplacementChar));
        }

        _utf8Length = 0;
        _utf8Expected = 0;
    }

    private void FlushPendingUtf8()
    {
        if (_utf8Length > 0)
        {
            _emit(new PrintRune(Rune.ReplacementChar));
            _utf8Length = 0;
            _utf8Expected = 0;
        }
    }

    // -- ESCAPE -----------------------------------------------------------

    private void EnterEscape()
    {
        _state = State.Escape;
        _intermediateCount = 0;
        _raw.Clear();
        _raw.Add(0x1B);
    }

    private void HandleEscape(byte b)
    {
        AppendRaw(b);

        // ESC [ — CSI
        if (b == (byte)'[')
        {
            EnterCsiEntry();
            return;
        }

        // ESC ] — OSC
        if (b == (byte)']')
        {
            EnterOscString();
            return;
        }

        // Two-byte ESC X singletons.
        switch (b)
        {
            case (byte)'7':
                _emit(new SaveCursor());
                EnterGround();
                return;
            case (byte)'8':
                _emit(new RestoreCursor());
                EnterGround();
                return;
            case (byte)'c':
                _emit(new ResetTerminal());
                EnterGround();
                return;
            case (byte)'D':
                _emit(new LineFeed());
                EnterGround();
                return; // IND
            case (byte)'E':
                _emit(new CarriageReturn());
                _emit(new LineFeed());
                EnterGround();
                return;
            case (byte)'M':
                _emit(new MoveCursorUp(1));
                EnterGround();
                return; // RI
            case (byte)'\\': // ST encountered outside OSC — ignore
                EnterGround();
                return;
        }

        // Intermediate bytes (0x20-0x2F) extend the escape — typically
        // character-set selection like "ESC ( B". We record them and
        // emit a diagnostic on the final byte.
        if (b >= 0x20 && b <= 0x2F)
        {
            if (_intermediateCount < _intermediates.Length)
            {
                _intermediates[_intermediateCount++] = b;
            }
            _state = State.EscapeIntermediate;
            return;
        }

        // Any other byte: emit diagnostic and return to ground.
        _emit(new UnknownSequence("ESC " + AsPrintable(b), SnapshotRaw()));
        EnterGround();
    }

    private void HandleEscapeIntermediate(byte b)
    {
        AppendRaw(b);

        if (b >= 0x20 && b <= 0x2F)
        {
            if (_intermediateCount < _intermediates.Length)
            {
                _intermediates[_intermediateCount++] = b;
            }
            return;
        }

        // Final byte for an intermediate-bearing ESC: e.g. character-set
        // selection "ESC ( B". We don't model character sets — surface as
        // UnknownSequence so traces can flag them.
        _emit(new UnknownSequence("ESC intermediate", SnapshotRaw()));
        EnterGround();
    }

    // -- CSI --------------------------------------------------------------

    private void EnterCsiEntry()
    {
        _state = State.CsiEntry;
        _paramCount = 0;
        _currentParamHasDigits = false;
        _intermediateCount = 0;
        _decPrivate = false;
        for (var i = 0; i < _params.Length; i++)
            _params[i] = 0;
    }

    private void HandleCsiEntry(byte b)
    {
        AppendRaw(b);

        if (b >= 0x30 && b <= 0x39) // digit
        {
            BeginParamWithDigit(b);
            _state = State.CsiParam;
            return;
        }

        if (b == (byte)';' || b == (byte)':')
        {
            // Empty leading param — push 0 and start a fresh slot.
            PushCurrentParam();
            _state = State.CsiParam;
            return;
        }

        if (b >= 0x3C && b <= 0x3F) // < = > ?
        {
            if (b == (byte)'?')
                _decPrivate = true;
            // Other private prefixes: ignore but stay in CSI flow.
            return;
        }

        if (b >= 0x20 && b <= 0x2F)
        {
            HandleCsiIntermediateByte(b);
            _state = State.CsiIntermediate;
            return;
        }

        if (b >= 0x40 && b <= 0x7E)
        {
            DispatchCsi(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void HandleCsiParam(byte b)
    {
        AppendRaw(b);

        if (b >= 0x30 && b <= 0x39)
        {
            ContinueParamWithDigit(b);
            return;
        }

        if (b == (byte)';' || b == (byte)':')
        {
            PushCurrentParam();
            return;
        }

        if (b >= 0x3C && b <= 0x3F)
        {
            // Private prefix mid-stream is illegal per spec — ignore.
            _state = State.CsiIgnore;
            return;
        }

        if (b >= 0x20 && b <= 0x2F)
        {
            PushCurrentParamIfPending();
            HandleCsiIntermediateByte(b);
            _state = State.CsiIntermediate;
            return;
        }

        if (b >= 0x40 && b <= 0x7E)
        {
            PushCurrentParamIfPending();
            DispatchCsi(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void HandleCsiIntermediate(byte b)
    {
        AppendRaw(b);

        if (b >= 0x20 && b <= 0x2F)
        {
            HandleCsiIntermediateByte(b);
            return;
        }

        if (b >= 0x40 && b <= 0x7E)
        {
            DispatchCsi(b);
            return;
        }

        _state = State.CsiIgnore;
    }

    private void HandleCsiIgnore(byte b)
    {
        AppendRaw(b);

        // Stay in ignore until a final byte; then drop to ground.
        if (b >= 0x40 && b <= 0x7E)
        {
            EnterGround();
        }
    }

    private void HandleCsiIntermediateByte(byte b)
    {
        if (_intermediateCount < _intermediates.Length)
        {
            _intermediates[_intermediateCount++] = b;
        }
    }

    private void BeginParamWithDigit(byte digit)
    {
        if (_paramCount >= MaxParams)
            return;
        _params[_paramCount] = digit - (byte)'0';
        _currentParamHasDigits = true;
    }

    private void ContinueParamWithDigit(byte digit)
    {
        if (_paramCount >= MaxParams)
            return;
        // Saturate at a generous cap to avoid overflow on malformed input.
        var next = (long)_params[_paramCount] * 10 + (digit - (byte)'0');
        _params[_paramCount] = next > 65535 ? 65535 : (int)next;
        _currentParamHasDigits = true;
    }

    private void PushCurrentParam()
    {
        if (_paramCount >= MaxParams)
            return;
        // _params[_paramCount] already holds 0 if no digits were seen, which
        // is the correct "default = 0" carrier for SGR-style sequences.
        _paramCount++;
        _currentParamHasDigits = false;
        if (_paramCount < MaxParams)
            _params[_paramCount] = 0;
    }

    private void PushCurrentParamIfPending()
    {
        if (_currentParamHasDigits)
        {
            PushCurrentParam();
        }
        else if (_paramCount < MaxParams && _params[_paramCount] != 0)
        {
            // Defensive: ensure a fresh slot is clean.
            _params[_paramCount] = 0;
        }
    }

    private int Param(int index, int defaultValue = 1)
    {
        if (index >= _paramCount)
            return defaultValue;
        var raw = _params[index];
        return raw == 0 ? defaultValue : raw;
    }

    private void DispatchCsi(byte finalByte)
    {
        // For SGR (m), missing parameters mean "0". For most cursor
        // motion commands, missing parameters mean "1". Param() handles
        // both modes via its defaultValue argument.

        switch ((char)finalByte)
        {
            case 'A':
                _emit(new MoveCursorUp(Param(0)));
                break;
            case 'B':
                _emit(new MoveCursorDown(Param(0)));
                break;
            case 'C':
                _emit(new MoveCursorForward(Param(0)));
                break;
            case 'D':
                _emit(new MoveCursorBack(Param(0)));
                break;
            case 'H':
            case 'f':
                _emit(new SetCursorPosition(Param(0), Param(1)));
                break;
            case 'J':
                _emit(new EraseInDisplay((EraseMode)Param(0, 0)));
                break;
            case 'K':
                _emit(new EraseInLine((EraseMode)Param(0, 0)));
                break;
            case 'S':
                _emit(new ScrollUp(Param(0)));
                break;
            case 'T':
                _emit(new ScrollDown(Param(0)));
                break;
            case 's':
                if (!_decPrivate)
                    _emit(new SaveCursor());
                else
                    EmitUnknownCsi(finalByte);
                break;
            case 'u':
                if (!_decPrivate)
                    _emit(new RestoreCursor());
                else
                    EmitUnknownCsi(finalByte);
                break;
            case 'm':
                EmitSgr();
                break;
            case 'h':
                EmitModeChanges(enabled: true);
                break;
            case 'l':
                EmitModeChanges(enabled: false);
                break;
            default:
                EmitUnknownCsi(finalByte);
                break;
        }

        EnterGround();
    }

    private void EmitUnknownCsi(byte finalByte)
    {
        _emit(new UnknownSequence("CSI " + AsPrintable(finalByte), SnapshotRaw()));
    }

    // -- mode set / reset -------------------------------------------------

    private void EmitModeChanges(bool enabled)
    {
        if (_paramCount == 0)
        {
            _emit(new SetMode(0, _decPrivate, enabled));
            return;
        }

        for (var i = 0; i < _paramCount; i++)
        {
            var mode = _params[i];
            if (_decPrivate)
            {
                switch (mode)
                {
                    case 25:
                        _emit(new SetCursorVisibility(enabled));
                        continue;
                    case 1049:
                        _emit(new SetUseAlternateScreen(enabled));
                        continue;
                    case 2004:
                        _emit(new SetBracketedPaste(enabled));
                        continue;
                }
            }
            _emit(new SetMode(mode, _decPrivate, enabled));
        }
    }

    // -- SGR --------------------------------------------------------------

    private void EmitSgr()
    {
        // Special case: zero parameters means "reset".
        if (_paramCount == 0)
        {
            _emit(new SetGraphicsRendition(new SgrParameter[] { new SgrReset() }));
            return;
        }

        var list = new List<SgrParameter>(_paramCount);
        var i = 0;
        while (i < _paramCount)
        {
            var p = _params[i];

            // Extended foreground / background.
            if ((p == 38 || p == 48) && i + 1 < _paramCount)
            {
                var sub = _params[i + 1];
                if (sub == 5 && i + 2 < _paramCount)
                {
                    var idx = Clamp255(_params[i + 2]);
                    list.Add(p == 38 ? new SgrForeground256(idx) : new SgrBackground256(idx));
                    i += 3;
                    continue;
                }
                if (sub == 2 && i + 4 < _paramCount)
                {
                    var r = (byte)Clamp255(_params[i + 2]);
                    var g = (byte)Clamp255(_params[i + 3]);
                    var b = (byte)Clamp255(_params[i + 4]);
                    list.Add(p == 38 ? new SgrForegroundRgb(r, g, b) : new SgrBackgroundRgb(r, g, b));
                    i += 5;
                    continue;
                }
                // Malformed extended colour — treat the lead as unknown
                // and skip just this parameter so the rest of the sequence
                // still parses.
                list.Add(new SgrUnknown(p));
                i++;
                continue;
            }

            list.Add(MapSimpleSgr(p));
            i++;
        }

        _emit(new SetGraphicsRendition(list));
    }

    private static SgrParameter MapSimpleSgr(int p) => p switch
    {
        0 => new SgrReset(),
        1 => new SgrBold(true),
        2 => new SgrDim(true),
        3 => new SgrItalic(true),
        4 => new SgrUnderline(true),
        7 => new SgrInverse(true),
        9 => new SgrStrikethrough(true),
        22 => new SgrBold(false),
        23 => new SgrItalic(false),
        24 => new SgrUnderline(false),
        27 => new SgrInverse(false),
        29 => new SgrStrikethrough(false),
        >= 30 and <= 37 => new SgrForegroundIndex(p - 30),
        39 => new SgrForegroundDefault(),
        >= 40 and <= 47 => new SgrBackgroundIndex(p - 40),
        49 => new SgrBackgroundDefault(),
        >= 90 and <= 97 => new SgrForegroundIndex(p - 90 + 8),
        >= 100 and <= 107 => new SgrBackgroundIndex(p - 100 + 8),
        _ => new SgrUnknown(p),
    };

    private static int Clamp255(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    // -- OSC --------------------------------------------------------------

    private void EnterOscString()
    {
        _state = State.OscString;
        _osc.Clear();
    }

    private void HandleOscString(byte b)
    {
        AppendRaw(b);

        if (b == 0x07) // BEL terminator
        {
            DispatchOsc();
            EnterGround();
            return;
        }

        if (b == 0x1B) // ESC — wait for backslash to make ST
        {
            _state = State.OscEsc;
            return;
        }

        if (_osc.Count < MaxOscLength)
        {
            _osc.Add(b);
        }
    }

    private void HandleOscEsc(byte b)
    {
        AppendRaw(b);

        if (b == (byte)'\\')
        {
            DispatchOsc();
            EnterGround();
            return;
        }

        // Anything else: abort the OSC, but the new byte may itself begin
        // a fresh sequence. We've already swallowed the ESC into _state
        // tracking via the anywhere-transition handler at the top of Step,
        // so simply re-process this byte by re-entering escape.
        EnterEscape();
        Step(b);
    }

    private void DispatchOsc()
    {
        // Most common form: "Ps;Pt" where Ps is a numeric command and Pt
        // is the payload. Window title is OSC 0 / OSC 1 / OSC 2.
        var bytes = _osc.ToArray();
        var separator = Array.IndexOf(bytes, (byte)';');
        if (separator <= 0)
        {
            _emit(new UnknownSequence("OSC", SnapshotRaw()));
            return;
        }

        if (!TryParseInt(bytes, 0, separator, out var command))
        {
            _emit(new UnknownSequence("OSC", SnapshotRaw()));
            return;
        }

        var payloadStart = separator + 1;
        var payload = Encoding.UTF8.GetString(bytes, payloadStart, bytes.Length - payloadStart);

        switch (command)
        {
            case 0:
            case 1:
            case 2:
                _emit(new SetWindowTitle(payload));
                break;
            default:
                _emit(new UnknownSequence("OSC " + command, SnapshotRaw()));
                break;
        }
    }

    private static bool TryParseInt(byte[] bytes, int start, int endExclusive, out int value)
    {
        value = 0;
        if (endExclusive <= start)
            return false;
        for (var i = start; i < endExclusive; i++)
        {
            var b = bytes[i];
            if (b < (byte)'0' || b > (byte)'9')
                return false;
            value = value * 10 + (b - (byte)'0');
            if (value > 1_000_000)
                return false;
        }
        return true;
    }

    // -- helpers ----------------------------------------------------------

    private void EnterGround()
    {
        _state = State.Ground;
        _raw.Clear();
        _osc.Clear();
        _paramCount = 0;
        _currentParamHasDigits = false;
        _intermediateCount = 0;
        _decPrivate = false;
    }

    private void AppendRaw(byte b)
    {
        if (_raw.Count < MaxRawSequenceLength)
        {
            _raw.Add(b);
        }
    }

    private byte[] SnapshotRaw() => _raw.ToArray();

    private static string AsPrintable(byte b) =>
        b is >= 0x20 and <= 0x7E ? ((char)b).ToString() : $"0x{b:X2}";

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        OscEsc,
    }
}
