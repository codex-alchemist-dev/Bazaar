using System.Buffers;
using System.Text;

namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>
/// A DEC VT500-style byte parser. It tracks the ground, escape, CSI, OSC and string states, decodes UTF-8 for printable
/// text and calls an <see cref="IVtSink"/>. Parameter count and OSC length are capped, and malformed or oversized
/// sequences are discarded silently. Nothing in the input makes the parser throw or grow its buffers.
/// </summary>
public sealed class VtParser
{
    /// <summary>Most CSI parameters kept. A sequence with more is discarded.</summary>
    public const int MaxParameters = 16;

    /// <summary>Most OSC payload bytes kept. A longer OSC string is discarded.</summary>
    public const int MaxOscBytes = 4096;

    /// <summary>Most intermediate bytes in one sequence. More discards the sequence.</summary>
    public const int MaxIntermediates = 2;

    private const int _maxParameterValue = 65535;
    private const byte _bel = 0x07;
    private const byte _can = 0x18;
    private const byte _sub = 0x1A;
    private const byte _esc = 0x1B;

    private enum State : byte
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        OscString,
        StringIgnore,
    }

    private readonly IVtSink _sink;
    private readonly int[] _params = new int[MaxParameters];
    private readonly byte[] _osc = new byte[MaxOscBytes];
    private readonly byte[] _utf8 = new byte[4];
    private State _state;
    private int _paramIndex;
    private bool _paramOverflow;
    private byte _prefix;
    private byte _intermediate;
    private int _intermediateCount;
    private bool _sequenceIgnored;
    private int _oscLength;
    private bool _oscOverflow;
    private int _utf8Length;
    private int _utf8Expected;
    private int _rejected;

    /// <summary>Creates a parser that reports to <paramref name="sink"/>.</summary>
    public VtParser(IVtSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <summary>Number of sequences discarded for size or malformation since creation (saturates at int.MaxValue).</summary>
    public int RejectedSequenceCount => _rejected;

    /// <summary>Returns the parser to the ground state, dropping any partial sequence.</summary>
    public void Reset()
    {
        _state = State.Ground;
        _utf8Length = 0;
        _utf8Expected = 0;
        _oscLength = 0;
        _oscOverflow = false;
        ResetCsi();
    }

    /// <summary>Feeds bytes. Sequences and UTF-8 characters may be split across calls.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            Consume(value);
        }
    }

    private void Consume(byte b)
    {
        if (b < 0x80 && _utf8Length > 0)
        {
            FlushUtf8();
        }

        switch (b)
        {
            case _can:
            case _sub:
                _state = State.Ground;
                return;
            case _esc:
                EnterEscape();
                return;
        }

        switch (_state)
        {
            case State.Ground:
                ConsumeGround(b);
                break;
            case State.Escape:
                ConsumeEscape(b);
                break;
            case State.EscapeIntermediate:
                ConsumeEscapeIntermediate(b);
                break;
            case State.CsiEntry:
                ConsumeCsiEntry(b);
                break;
            case State.CsiParam:
                ConsumeCsiParam(b);
                break;
            case State.CsiIntermediate:
                ConsumeCsiIntermediate(b);
                break;
            case State.CsiIgnore:
                ConsumeCsiIgnore(b);
                break;
            case State.OscString:
                ConsumeOsc(b);
                break;
            case State.StringIgnore:
                break;
        }
    }

    private void ConsumeGround(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b < 0x7F)
        {
            _sink.Print(new Rune((char)b));
        }
        else if (b >= 0x80)
        {
            ConsumeHighByte(b);
        }
    }

    private void ConsumeHighByte(byte b)
    {
        if (_utf8Length == 0)
        {
            _utf8Expected = b switch
            {
                >= 0xC2 and <= 0xDF => 2,
                >= 0xE0 and <= 0xEF => 3,
                >= 0xF0 and <= 0xF4 => 4,
                _ => 0,
            };

            if (_utf8Expected == 0)
            {
                _sink.Print(Rune.ReplacementChar);
                return;
            }

            _utf8[0] = b;
            _utf8Length = 1;
            return;
        }

        if ((b & 0xC0) != 0x80)
        {
            FlushUtf8();
            ConsumeHighByte(b);
            return;
        }

        _utf8[_utf8Length++] = b;
        if (_utf8Length == _utf8Expected)
        {
            OperationStatus status = Rune.DecodeFromUtf8(_utf8.AsSpan(0, _utf8Length), out Rune rune, out _);
            _sink.Print(status == OperationStatus.Done ? rune : Rune.ReplacementChar);
            _utf8Length = 0;
            _utf8Expected = 0;
        }
    }

    private void FlushUtf8()
    {
        if (_utf8Length > 0)
        {
            _sink.Print(Rune.ReplacementChar);
            _utf8Length = 0;
            _utf8Expected = 0;
        }
    }

    private void EnterEscape()
    {
        if (_state == State.OscString)
        {
            DispatchOsc();
        }

        _intermediate = 0;
        _intermediateCount = 0;
        _sequenceIgnored = false;
        _state = State.Escape;
    }

    private void ConsumeEscape(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b <= 0x2F)
        {
            CollectIntermediate(b);
            _state = State.EscapeIntermediate;
        }
        else if (b <= 0x7E)
        {
            switch (b)
            {
                case (byte)'[':
                    ResetCsi();
                    _state = State.CsiEntry;
                    break;
                case (byte)']':
                    EnterOsc();
                    break;
                case (byte)'P':
                case (byte)'X':
                case (byte)'^':
                case (byte)'_':
                    _state = State.StringIgnore;
                    break;
                default:
                    _sink.EscDispatch(0, b);
                    _state = State.Ground;
                    break;
            }
        }
    }

    private void ConsumeEscapeIntermediate(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b <= 0x2F)
        {
            CollectIntermediate(b);
        }
        else if (b <= 0x7E)
        {
            if (_sequenceIgnored)
            {
                CountRejected();
            }
            else
            {
                _sink.EscDispatch(_intermediate, b);
            }

            _state = State.Ground;
        }
    }

    private void ConsumeCsiEntry(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b <= 0x2F)
        {
            CollectIntermediate(b);
            _state = State.CsiIntermediate;
        }
        else if (b <= 0x3B)
        {
            _state = State.CsiParam;
            ParamByte(b);
        }
        else if (b <= 0x3F)
        {
            _prefix = b;
            _state = State.CsiParam;
        }
        else if (b <= 0x7E)
        {
            DispatchCsi(b);
            _state = State.Ground;
        }
    }

    private void ConsumeCsiParam(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b <= 0x2F)
        {
            CollectIntermediate(b);
            _state = State.CsiIntermediate;
        }
        else if (b <= 0x3B)
        {
            ParamByte(b);
        }
        else if (b <= 0x3F)
        {
            EnterCsiIgnore();
        }
        else if (b <= 0x7E)
        {
            DispatchCsi(b);
            _state = State.Ground;
        }
    }

    private void ConsumeCsiIntermediate(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b <= 0x2F)
        {
            CollectIntermediate(b);
        }
        else if (b <= 0x3F)
        {
            EnterCsiIgnore();
        }
        else if (b <= 0x7E)
        {
            DispatchCsi(b);
            _state = State.Ground;
        }
    }

    private void ConsumeCsiIgnore(byte b)
    {
        if (b < 0x20)
        {
            _sink.Execute(b);
        }
        else if (b >= 0x40 && b <= 0x7E)
        {
            _state = State.Ground;
        }
    }

    private void ConsumeOsc(byte b)
    {
        if (b == _bel)
        {
            DispatchOsc();
            _state = State.Ground;
            return;
        }

        if (b < 0x20 || b == 0x7F)
        {
            return;
        }

        if (_oscLength < MaxOscBytes)
        {
            _osc[_oscLength++] = b;
        }
        else if (!_oscOverflow)
        {
            _oscOverflow = true;
            CountRejected();
        }
    }

    private void EnterOsc()
    {
        _oscLength = 0;
        _oscOverflow = false;
        _state = State.OscString;
    }

    private void DispatchOsc()
    {
        if (!_oscOverflow)
        {
            _sink.OscDispatch(_osc.AsSpan(0, _oscLength));
        }

        _oscLength = 0;
        _oscOverflow = false;
    }

    private void ResetCsi()
    {
        _paramIndex = 0;
        _params[0] = 0;
        _paramOverflow = false;
        _prefix = 0;
        _intermediate = 0;
        _intermediateCount = 0;
        _sequenceIgnored = false;
    }

    private void ParamByte(byte b)
    {
        if (b == (byte)';' || b == (byte)':')
        {
            if (_paramIndex + 1 >= MaxParameters)
            {
                _paramOverflow = true;
            }
            else
            {
                _paramIndex++;
                _params[_paramIndex] = 0;
            }

            return;
        }

        _params[_paramIndex] = Math.Min((_params[_paramIndex] * 10) + (b - '0'), _maxParameterValue);
    }

    private void CollectIntermediate(byte b)
    {
        if (_intermediateCount == 0)
        {
            _intermediate = b;
        }

        if (_intermediateCount < MaxIntermediates)
        {
            _intermediateCount++;
        }
        else
        {
            _sequenceIgnored = true;
        }
    }

    private void EnterCsiIgnore()
    {
        CountRejected();
        _state = State.CsiIgnore;
    }

    private void DispatchCsi(byte final)
    {
        if (_paramOverflow || _sequenceIgnored)
        {
            CountRejected();
            return;
        }

        _sink.CsiDispatch(new CsiSequence(_params.AsSpan(0, _paramIndex + 1), _prefix, _intermediate, final));
    }

    private void CountRejected()
    {
        if (_rejected < int.MaxValue)
        {
            _rejected++;
        }
    }
}
