namespace Bazaar.Server.Portability.Sandbox;

/// <summary>Why a sandboxed parser job failed. A failure never writes to the instance.</summary>
public enum ParserFailureKind
{
    /// <summary>The child sent no frame for the frame-gap allowance and its tree was killed. Counts toward the crash loop.</summary>
    Timeout,

    /// <summary>The whole job ran past the job timeout and the tree was killed. Does not count toward the crash loop, because a legitimately slow parse of a big source ends this way too.</summary>
    JobTimeout,

    /// <summary>The parser host rejected the request (exit code 2). The caller's input, not a crash.</summary>
    BadRequest,

    /// <summary>The child ended without a result: a crash, a kill by the OS (memory or CPU ceiling), or a bad exit.</summary>
    Crash,

    /// <summary>The child wrote more than the output cap or sent an oversize frame.</summary>
    OutputTooLarge,

    /// <summary>The child sent something that is not the protocol.</summary>
    ProtocolViolation,

    /// <summary>The source broke an import limit or an archive safety rule (a zip bomb, an oversize record), or the child ran out of memory.</summary>
    LimitExceeded,

    /// <summary>The importer threw, or is not known to the child.</summary>
    ParserError,

    /// <summary>The crash-loop circuit for this importer is open.</summary>
    CrashLoop,

    /// <summary>Full confinement was required and the provider could not apply all of it.</summary>
    ConfinementUnavailable,

    /// <summary>The parser host could not be started.</summary>
    HostUnavailable,
}

/// <summary>A sandboxed parser job failed cleanly. The import pipeline reports it as a failed job.</summary>
public sealed class ParserSandboxException : Exception
{
    public ParserSandboxException(ParserFailureKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public ParserSandboxException(ParserFailureKind kind, string message, Exception innerException) : base(message, innerException)
    {
        Kind = kind;
    }

    public ParserSandboxException() : this(ParserFailureKind.Crash, "parser sandbox failure")
    {
    }

    public ParserSandboxException(string message) : this(ParserFailureKind.Crash, message)
    {
    }

    public ParserSandboxException(string message, Exception innerException) : this(ParserFailureKind.Crash, message, innerException)
    {
    }

    public ParserFailureKind Kind { get; }

    /// <summary>True when the failure is the source's doing and counts against that source's retry cap.</summary>
    public bool CountsAgainstSource => Kind is not (ParserFailureKind.ConfinementUnavailable or ParserFailureKind.HostUnavailable
        or ParserFailureKind.CrashLoop or ParserFailureKind.BadRequest);

    /// <summary>True when this ending counts toward the crash-loop circuit: the child itself misbehaved.</summary>
    public bool CountsAsCrash => Kind is ParserFailureKind.Timeout or ParserFailureKind.Crash
        or ParserFailureKind.OutputTooLarge or ParserFailureKind.ProtocolViolation;
}

/// <summary>A signal raised to the health and alert path (doc 46 hooks).</summary>
public sealed record ParserSignal(string Name, string ImporterId, string Detail);

/// <summary>Names of the parser signals.</summary>
public static class ParserSignals
{
    /// <summary>An importer's child ended abnormally repeatedly within the window.</summary>
    public const string CrashLoop = "parser_crash_loop";

    /// <summary>A job was refused because the confinement provider could not apply a required item (for example no-network).</summary>
    public const string ConfinementMissing = "parser_confinement_missing";
}

/// <summary>Receives parser signals. The server host maps them to the alert path of doc 46.</summary>
public interface IParserSignalSink
{
    void Raise(ParserSignal signal);
}
