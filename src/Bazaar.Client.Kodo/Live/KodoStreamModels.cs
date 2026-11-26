using System.Text.Json;

namespace Bazaar.Client.Kodo.Live;

/// <summary>One component's state as the server sent it (doc 46 section 15.1). Strings stay as sent until the tracker checks them.</summary>
public sealed record KodoComponentWire(string Id, string State, string? Cause, DateTimeOffset Since);

/// <summary>The status snapshot on the wire (doc 46 sections 12.2 and 15.1).</summary>
public sealed record KodoSnapshotWire(
    int SchemaVersion,
    long Revision,
    DateTimeOffset GeneratedAt,
    string State,
    string Underlying,
    int Score,
    string? JournalHead,
    IReadOnlyList<KodoComponentWire> Components);

/// <summary>A change of the displayed state (doc 46 section 15.2).</summary>
public sealed record KodoStateChangeWire(string Previous, string State, string Underlying);

/// <summary>One frame of the <c>kodo</c> stream: a state change, a snapshot or a heartbeat.</summary>
public sealed record KodoFrameWire(long Revision, string Type, DateTimeOffset At, KodoStateChangeWire? StateChange, KodoSnapshotWire? Snapshot);

/// <summary>
/// Reads the read API's snapshot and the stream's frames in the server's own shape (<c>Bazaar.Contracts.Kodo</c>: the snapshot nests
/// a <c>status</c> object) and maps them to the client's flat records. A body it cannot read yields false and a reason, never a guess.
/// </summary>
public static class KodoStreamCodec
{
    /// <summary>Reads one snapshot body.</summary>
    public static bool TryReadSnapshot(ReadOnlySpan<byte> utf8, out KodoSnapshotWire? snapshot, out string? error)
    {
        snapshot = null;
        Contracts.Kodo.KodoStatusSnapshotDto? dto;
        try
        {
            dto = Contracts.Kodo.KodoWireCodec.DecodeSnapshot(utf8);
        }
        catch (JsonException ex)
        {
            error = $"The Kodo body could not be read: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "The Kodo body was empty.";
            return false;
        }

        snapshot = Map(dto);
        error = null;
        return true;
    }

    /// <summary>Reads one stream frame.</summary>
    public static bool TryReadFrame(ReadOnlySpan<byte> utf8, out KodoFrameWire? frame, out string? error)
    {
        frame = null;
        Contracts.Kodo.KodoStreamEventDto? dto;
        try
        {
            dto = Contracts.Kodo.KodoWireCodec.DecodeEvent(utf8);
        }
        catch (JsonException ex)
        {
            error = $"The Kodo body could not be read: {ex.Message}";
            return false;
        }

        if (dto is null)
        {
            error = "The Kodo body was empty.";
            return false;
        }

        var change = dto.StateChange is { } c ? new KodoStateChangeWire(c.Previous, c.State, c.UnderlyingState ?? c.State) : null;
        frame = new KodoFrameWire(dto.Revision, dto.Type, dto.At, change, dto.Snapshot is { } s ? Map(s) : null);
        error = null;
        return true;
    }

    private static KodoSnapshotWire Map(Contracts.Kodo.KodoStatusSnapshotDto s) =>
        new(
            s.SchemaVersion,
            s.Revision,
            s.Status.AsOf,
            s.Status.State,
            s.Status.UnderlyingState ?? s.Status.State,
            s.Status.Score,
            s.JournalHead,
            [.. s.Components.Select(c => new KodoComponentWire(c.Id, c.State, c.Cause, c.Since))]);
}
