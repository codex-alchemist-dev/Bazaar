namespace Bazaar.Contracts.Wire;

/// <summary>Stream names (<c>s</c>). Only <c>gc:&lt;id&gt;</c> is a replayed log; the rest are state (doc 02, stream registry).</summary>
public static class WireStreams
{
    /// <summary>Prefix of a GC log stream.</summary>
    public const string GcPrefix = "gc:";

    /// <summary>Prefix of a Kanban board stream.</summary>
    public const string BoardPrefix = "board:";

    /// <summary>Prefix of a graph stream.</summary>
    public const string GraphPrefix = "graph:";

    /// <summary>Presence state.</summary>
    public const string Presence = "presence";

    /// <summary>Server-driven UI state.</summary>
    public const string Ui = "ui";

    /// <summary>Server-streamed agent terminal panes.</summary>
    public const string Pane = "pane";

    /// <summary>Pending owner and Tier 2 decisions.</summary>
    public const string Approvals = "approvals";

    /// <summary>The stream name for a GC.</summary>
    public static string Gc(Guid gcId) => GcPrefix + gcId.ToString("D");

    /// <summary>Reads the GC id out of a <c>gc:&lt;id&gt;</c> stream name.</summary>
    public static bool TryParseGc(string? stream, out Guid gcId)
    {
        gcId = default;
        return stream is not null
            && stream.StartsWith(GcPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(stream.AsSpan(GcPrefix.Length), "D", out gcId);
    }
}
