using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Wire;

/// <summary>
/// The client's per-stream cursor: the last <c>n</c> and <c>ev</c> applied (doc 02, replay after reconnect).
/// The client's cursor is authoritative for replay. Persistence is the implementation's job.
/// </summary>
public interface ICursorStore
{
    /// <summary>The cursor for a stream, or zeros when none is stored.</summary>
    StreamCursor Load(string stream);

    /// <summary>Stores a cursor.</summary>
    void Save(StreamCursor cursor);
}

/// <summary>Cursors held in memory only.</summary>
public sealed class InMemoryCursorStore : ICursorStore
{
    private readonly Dictionary<string, StreamCursor> _cursors = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public StreamCursor Load(string stream)
    {
        lock (_gate)
        {
            return _cursors.TryGetValue(stream, out var c) ? c : new StreamCursor(stream, 0, 0);
        }
    }

    /// <inheritdoc />
    public void Save(StreamCursor cursor)
    {
        lock (_gate)
        {
            _cursors[cursor.S] = cursor;
        }
    }
}
