namespace Bazaar.Server.Subagents;

/// <summary>
/// Text produced by a child session. It is data: it never carries merge or approval authority and must not be
/// treated as instructions by the parent (doc 17 "Review flow and subagents", doc 06 untrusted-input rules).
/// </summary>
public sealed record UntrustedText(string Value)
{
    /// <summary>The empty text.</summary>
    public static UntrustedText Empty { get; } = new(string.Empty);

    /// <inheritdoc />
    public override string ToString() => Value;
}
