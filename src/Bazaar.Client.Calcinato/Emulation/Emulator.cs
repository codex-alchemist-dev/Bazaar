using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>
/// The in-tree VT emulator (doc 52, section 5.1): a <see cref="VtParser"/> driving a <see cref="ScreenBuffer"/>, exposed
/// as <see cref="IScreen"/>. Pure and testable without a display. Device replies (DA1, DSR 6) are rate-limited before
/// they reach <see cref="Response"/>, so a hostile stream cannot make the terminal flood the shell.
/// </summary>
public sealed class Emulator : IScreen
{
    /// <summary>Device replies allowed per second, with the same figure as the burst size.</summary>
    public const int ResponsesPerSecond = 16;

    private const int _responseBurst = 16;

    private readonly ScreenBuffer _screen;
    private readonly VtParser _parser;
    private readonly TimeProvider _timeProvider;
    private double _tokens = _responseBurst;
    private long _lastRefill;

    /// <summary>Creates an emulator using the system clock for response rate limiting.</summary>
    /// <param name="cols">Columns.</param>
    /// <param name="rows">Rows.</param>
    /// <param name="scrollbackLimit">Scrollback lines kept above the screen, capped at <see cref="ScreenBuffer.MaxScrollbackLines"/>.</param>
    public Emulator(int cols, int rows, int scrollbackLimit = 10000)
        : this(cols, rows, scrollbackLimit, TimeProvider.System)
    {
    }

    /// <summary>Creates an emulator with an explicit clock, used by tests for deterministic rate limiting.</summary>
    /// <param name="cols">Columns.</param>
    /// <param name="rows">Rows.</param>
    /// <param name="scrollbackLimit">Scrollback lines kept above the screen.</param>
    /// <param name="timeProvider">Clock for the response rate limit.</param>
    public Emulator(int cols, int rows, int scrollbackLimit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _lastRefill = timeProvider.GetTimestamp();
        _screen = new ScreenBuffer(cols, rows, scrollbackLimit);
        _screen.Reply += OnReply;
        _parser = new VtParser(_screen);
    }

    /// <summary>Device replies (DA1 and DSR 6) to write back to the shell. Rate-limited; excess replies are dropped.</summary>
    public event Action<ReadOnlyMemory<byte>>? Response;

    /// <summary>Replies dropped by the rate limit since creation.</summary>
    public int ResponsesDropped { get; private set; }

    /// <summary>Sequences the parser discarded for size or malformation since creation.</summary>
    public int RejectedSequenceCount => _parser.RejectedSequenceCount;

    /// <inheritdoc />
    public int Cols => _screen.Cols;

    /// <inheritdoc />
    public int Rows => _screen.Rows;

    /// <inheritdoc />
    public int ScrollbackCount => _screen.ScrollbackCount;

    /// <inheritdoc />
    public bool IsAlternateScreen => _screen.IsAlternateScreen;

    /// <inheritdoc />
    public int CursorRow => _screen.CursorRow;

    /// <inheritdoc />
    public int CursorCol => _screen.CursorCol;

    /// <inheritdoc />
    public string Title => _screen.Title;

    /// <summary>DECCKM: application cursor keys.</summary>
    public bool ApplicationCursorKeys => _screen.ApplicationCursorKeys;

    /// <summary>DECSET 2004: bracketed paste.</summary>
    public bool BracketedPaste => _screen.BracketedPaste;

    /// <summary>DECTCEM: cursor visible.</summary>
    public bool CursorVisible => _screen.CursorVisible;

    /// <summary>Mouse reporting modes requested by the program (1000, 1002, 1006), as flags.</summary>
    public MouseModes MouseMode => _screen.MouseMode;

    /// <summary>DECAWM: autowrap.</summary>
    public bool AutoWrap => _screen.AutoWrap;

    /// <summary>DECOM: origin mode.</summary>
    public bool OriginMode => _screen.OriginMode;

    /// <summary>
    /// Hyperlink spans on the active screen (OSC 8). Stored only; the user decides whether to open one. The row is an
    /// absolute row, scrollback first.
    /// </summary>
    public IReadOnlyList<(string Uri, int Row, int StartCol, int EndCol)> Hyperlinks => _screen.Hyperlinks;

    /// <summary>Feeds program output. UTF-8 characters and escape sequences may be split across calls.</summary>
    public void Feed(ReadOnlySpan<byte> bytes) => _parser.Feed(bytes);

    /// <summary>Resizes the screen. The primary screen reflows; the alternate screen keeps its content.</summary>
    public void Resize(int cols, int rows) => _screen.Resize(cols, rows);

    /// <summary>The cell at an absolute row (scrollback first) and column. Out of range returns a blank cell.</summary>
    public Cell GetCell(int absoluteRow, int col) => _screen.GetCell(absoluteRow, col);

    /// <inheritdoc />
    public string GetLineText(int absoluteRow) => _screen.GetLineText(absoluteRow);

    private void OnReply(ReadOnlyMemory<byte> reply)
    {
        long now = _timeProvider.GetTimestamp();
        double seconds = (now - _lastRefill) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (seconds > 0)
        {
            _tokens = Math.Min(_responseBurst, _tokens + (seconds * ResponsesPerSecond));
            _lastRefill = now;
        }

        if (_tokens < 1)
        {
            if (ResponsesDropped < int.MaxValue)
            {
                ResponsesDropped++;
            }

            return;
        }

        _tokens -= 1;
        Response?.Invoke(reply);
    }
}
