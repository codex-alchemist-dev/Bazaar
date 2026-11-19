namespace Bazaar.Diagnostics.Ingestion;

/// <summary>The state of a report's symbols, stored as <c>symbols_state</c> (doc 43, section 12.1 step 6).</summary>
public enum SymbolsState
{
    /// <summary>The release symbol bundle for the build id resolved the flagged frame.</summary>
    Resolved,

    /// <summary>No symbols were found. The report is still kept.</summary>
    Missing,
}

/// <summary>
/// Resolves the flagged frame of a report against the release symbol bundle for its build id (doc 43, section 12.1
/// step 6). The bundle lookup is supplied by the caller (doc 37); this interface is the seam that keeps the pipeline pure.
/// </summary>
public interface ISymbolicator
{
    /// <summary>Returns <see cref="SymbolsState.Resolved"/> when the symbols for the report's build are available, otherwise <see cref="SymbolsState.Missing"/>.</summary>
    SymbolsState Resolve(Bazaar.Contracts.Telemetry.CrashReport report);
}
