using System.Collections.Immutable;
using Bazaar.Server.Security.Readers;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.ReaderSessions;

/// <summary>Why a request was refused before any model call or scratch write.</summary>
public enum ReaderRefusalReason
{
    /// <summary>The request tried to attach a tool. Reader sessions offer none.</summary>
    ToolsRequested,

    /// <summary>The request tried to attach a credential. Reader sessions hold none.</summary>
    CredentialsRequested,

    /// <summary>The agent id, purpose or model policy key is blank.</summary>
    MissingField,

    /// <summary>The input text is blank.</summary>
    BlankInput,

    /// <summary>The input text is longer than the configured cap.</summary>
    InputTooLarge,

    /// <summary>The named hand-over schema is not registered.</summary>
    UnknownSchema,

    /// <summary>Structured mode needs at least one source ref, so the hand-over has lineage.</summary>
    MissingSourceRefs,
}

/// <summary>
/// The outcome of one reader session. Every result carries the session id, the taint (always hard) and the source refs
/// lineage. No result carries scratch contents: scratch is purged before the result is returned.
/// </summary>
public abstract record ReaderSessionResult(Guid SessionId, SessionTaint Taint, ImmutableArray<Guid> SourceRefs)
{
    /// <summary>
    /// The model replied and, in structured mode, the payload passed its schema. Plain mode: <see cref="Text"/> is untrusted
    /// text the caller must validate itself. Structured mode: <see cref="Handover"/> holds only validated fields and
    /// <see cref="Text"/> is null.
    /// </summary>
    public sealed record Completed(
        Guid SessionId, SessionTaint Taint, ImmutableArray<Guid> SourceRefs, string? Text, AcceptedHandover? Handover)
        : ReaderSessionResult(SessionId, Taint, SourceRefs);

    /// <summary>The payload failed validation. It is held and never released; <see cref="State"/> is quarantined and carries no fields.</summary>
    public sealed record Quarantined(Guid SessionId, SessionTaint Taint, ImmutableArray<Guid> SourceRefs, HandoverState State)
        : ReaderSessionResult(SessionId, Taint, SourceRefs);

    /// <summary>The gateway failed, the stream was cut, the model called a tool, or a cap was hit. Nothing was produced.</summary>
    public sealed record ModelFailed(Guid SessionId, SessionTaint Taint, ImmutableArray<Guid> SourceRefs, string Detail)
        : ReaderSessionResult(SessionId, Taint, SourceRefs);

    /// <summary>The request was refused before anything ran.</summary>
    public sealed record Refused(Guid SessionId, SessionTaint Taint, ImmutableArray<Guid> SourceRefs, ReaderRefusalReason Reason)
        : ReaderSessionResult(SessionId, Taint, SourceRefs);
}

/// <summary>Runs reader sessions. A session lives for one call: open, model call, validate, purge, result.</summary>
public interface IReaderSessionRunner
{
    /// <summary>Runs one session. Scratch is purged on every path, including exceptions and cancellation.</summary>
    Task<ReaderSessionResult> RunAsync(ReaderSessionRequest request, CancellationToken ct);
}
