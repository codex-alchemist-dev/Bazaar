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

    /// <summary>Kodo health state (doc 46, section 15.2).</summary>
    public const string Kodo = "kodo";

    /// <summary>Server-driven UI state.</summary>
    public const string Ui = "ui";

    /// <summary>Server-streamed agent terminal panes.</summary>
    public const string Pane = "pane";

    /// <summary>Prefix of a terminal pane stream (<c>pane:&lt;terminalId&gt;</c>).</summary>
    public const string PanePrefix = "pane:";

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

    /// <summary>The stream name for a terminal pane.</summary>
    /// <exception cref="ArgumentException"><paramref name="terminalId"/> is not a valid terminal id.</exception>
    public static string Terminal(string terminalId)
    {
        if (!IsValidTerminalId(terminalId))
        {
            throw new ArgumentException("A terminal id is 1 to 64 characters: letters, digits, '_' or '-'.", nameof(terminalId));
        }

        return PanePrefix + terminalId;
    }

    /// <summary>Reads the terminal id out of a <c>pane:&lt;terminalId&gt;</c> stream name.</summary>
    public static bool TryParseTerminal(string? stream, out string terminalId)
    {
        terminalId = string.Empty;
        if (stream is null || !stream.StartsWith(PanePrefix, StringComparison.Ordinal)) return false;

        var id = stream[PanePrefix.Length..];
        if (!IsValidTerminalId(id)) return false;

        terminalId = id;
        return true;
    }

    /// <summary>True when <paramref name="terminalId"/> is 1 to 64 characters, each an ASCII letter, digit, <c>_</c> or <c>-</c>.</summary>
    public static bool IsValidTerminalId(string? terminalId)
    {
        if (string.IsNullOrEmpty(terminalId) || terminalId.Length > 64) return false;

        foreach (var c in terminalId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) return false;
        }

        return true;
    }

    /// <summary>The stream name for a graph.</summary>
    public static string Graph(Guid graphId) => GraphPrefix + graphId.ToString("D");

    /// <summary>Reads the graph id out of a <c>graph:&lt;id&gt;</c> stream name.</summary>
    public static bool TryParseGraph(string? stream, out Guid graphId)
    {
        graphId = default;
        return stream is not null
            && stream.StartsWith(GraphPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(stream.AsSpan(GraphPrefix.Length), "D", out graphId);
    }
}
