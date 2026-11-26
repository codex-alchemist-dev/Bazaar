namespace Bazaar.Server.CodingReview;

/// <summary>Where a piece of third-party text came from. Every source is untrusted; none can carry instructions.</summary>
public enum UntrustedTextSource
{
    /// <summary>A hunk of the PR diff, written by the author.</summary>
    Diff,

    /// <summary>A file path in the PR diff.</summary>
    Path,

    /// <summary>The task card title or summary, written by a person or an agent.</summary>
    TaskCard,

    /// <summary>A message in the GC thread that asked for the work.</summary>
    GcThread,

    /// <summary>Detail text from the server-side check runner, which can echo diff text.</summary>
    CheckOutput,
}

/// <summary>
/// Third-party text (diff, task card, GC thread, check output) wrapped so that consumers cannot treat it as instructions.
/// There is no implicit conversion to <see cref="string"/>. A consumer must read <see cref="Text"/> explicitly, and the wrapper
/// keeps its <see cref="Source"/> for the reviewer prompt. Doc 17, Review flow and subagents; doc 15, untrusted input.
/// </summary>
public sealed record UntrustedText(UntrustedTextSource Source, string Text)
{
    /// <summary>Wraps <paramref name="text"/> from <paramref name="source"/>. Null becomes empty text.</summary>
    public static UntrustedText From(UntrustedTextSource source, string? text) => new(source, text ?? string.Empty);

    /// <summary>Length of the wrapped text in characters.</summary>
    public int Length => Text.Length;
}
