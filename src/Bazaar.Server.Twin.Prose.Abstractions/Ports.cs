namespace Bazaar.Server.Twin.Prose;

/// <summary>One data block handed to a reader session. <see cref="Content"/> is quoted data, never an instruction.</summary>
public sealed record ReaderDataBlock(string Label, string OriginKind, TwinTrust Trust, string Content);

/// <summary>A reader-session request: fixed instructions written by the server, and labelled data.</summary>
public sealed record ReaderRequest(string Instructions, IReadOnlyList<ReaderDataBlock> Data);

/// <summary>One drafted sentence and the source it claims to come from.</summary>
public sealed record DraftSentence(string Text, string? SourceRef);

/// <summary>The reader's prose draft. Nothing in it is trusted until the validator passes it.</summary>
public sealed record ReaderDraft(IReadOnlyList<DraftSentence> Sentences);

/// <summary>Thrown by a reader session that could not produce a draft.</summary>
public sealed class ReaderSessionException : Exception
{
    public ReaderSessionException(string message) : base(message) { }

    public ReaderSessionException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A tool-less model call (doc 15 reader/actor split). It gets labelled data and returns a prose draft. It has no tools and
/// takes no actions. Implementations throw <see cref="ReaderSessionException"/> on failure.
/// </summary>
public interface IReaderSession
{
    Task<ReaderDraft> ReadAsync(ReaderRequest request, CancellationToken ct);
}

/// <summary>Reads the Twin facts of a project (the rebuildable Postgres index).</summary>
public interface ITwinFactSource
{
    Task<IReadOnlyList<TwinFactRecord>> ReadFactsAsync(string project, CancellationToken ct);
}

/// <summary>A written page version.</summary>
public sealed record TwinPageRef(string Path, int Version);

/// <summary>A page to write. <see cref="Markdown"/> is the full file, front matter included.</summary>
public sealed record TwinPageWrite(string Project, string Page, string Path, string Markdown);

/// <summary>Writes Twin pages through the Vault writer (doc 18). Pages are generated and never authoritative.</summary>
public interface ITwinPageWriter
{
    /// <summary>The latest written version of a page, or null when none exists.</summary>
    Task<TwinPageRef?> GetLatestAsync(string project, string page, CancellationToken ct);

    Task<TwinPageRef> WriteAsync(TwinPageWrite page, CancellationToken ct);
}

/// <summary>Why a prose run ended.</summary>
public enum TwinProseOutcome
{
    /// <summary>A page was written.</summary>
    Written,

    /// <summary>The project has no facts the reader may see. Nothing was written.</summary>
    NoFacts,

    /// <summary>Every drafted sentence was dropped. Nothing was written.</summary>
    NoValidSentences,

    /// <summary>The reader session failed. Nothing was written (fail closed).</summary>
    ReaderFailed,
}

/// <summary>Result of one prose run.</summary>
/// <param name="Outcome">Why the run ended.</param>
/// <param name="Page">The written page, or null when nothing was written.</param>
/// <param name="Trust">Trust of the written page (highest trust of the facts it used), or null.</param>
/// <param name="Accepted">Sentences kept.</param>
/// <param name="Dropped">Sentences dropped by the validator.</param>
/// <param name="Error">A short reason for <see cref="TwinProseOutcome.ReaderFailed"/>.</param>
public sealed record TwinProseResult(TwinProseOutcome Outcome, TwinPageRef? Page, TwinTrust? Trust, int Accepted, int Dropped, string? Error = null);

/// <summary>The Twin prose step (doc 25 WP4).</summary>
public interface ITwinProseStep
{
    Task<TwinProseResult> RunAsync(string project, CancellationToken ct);
}
