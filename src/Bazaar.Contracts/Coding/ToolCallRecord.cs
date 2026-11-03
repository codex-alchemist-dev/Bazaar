namespace Bazaar.Contracts.Coding;

/// <summary>
/// One row of the append-only <c>tool_calls</c> table, written by the host (docs/design/17-coding-agent-harness.md, data).
/// Arguments are stored as a hash only, never as text.
/// </summary>
public sealed record ToolCallRecord(
    Guid Id,
    string SessionId,
    string Tool,

    /// <summary>The name of a Bazaar.Server CapabilityClass member. Contracts does not reference Server, so this is a string.</summary>
    string CapabilityClass,

    /// <summary>The source trust label from docs/design/15 (see SourceTrust).</summary>
    string SourceTrust,
    bool TaintedAtWrite,
    ToolCallStatus Status,
    int? ExitCode,
    string ArgsSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);
