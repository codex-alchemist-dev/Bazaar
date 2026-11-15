namespace Bazaar.Diagnostics.Redaction;

/// <summary>How a redaction pass ended.</summary>
public enum RedactionStatus
{
    /// <summary>Redaction completed and the residual scan found nothing.</summary>
    Ok,

    /// <summary>A step threw. The text is <see cref="RedactionMarkers.Failed"/>.</summary>
    Failed,

    /// <summary>The residual scan matched after redaction. The text is <see cref="RedactionMarkers.Residual"/>.</summary>
    Residual,
}

/// <summary>Hit counts for one string, matching the report's <c>redaction.counts</c> fields.</summary>
/// <param name="Pattern">Matches replaced by a pattern rule.</param>
/// <param name="Exact">Matches of runtime exact values.</param>
/// <param name="Structural">Fields dropped by class.</param>
/// <param name="Truncated">Strings cut at the length limit.</param>
public sealed record RedactionCounts(int Pattern, int Exact, int Structural, int Truncated)
{
    /// <summary>No hits.</summary>
    public static RedactionCounts None { get; } = new(0, 0, 0, 0);
}

/// <summary>The output of <see cref="Redactor.Redact"/> for one string.</summary>
public sealed record RedactionResult(string Text, RedactionCounts Counts, RedactionStatus Status);
