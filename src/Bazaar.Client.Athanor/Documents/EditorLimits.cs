namespace Bazaar.Client.Athanor;

/// <summary>
/// Size and content limits for opening a document (doc 57 §5). Documents above <see cref="MaxOpenBytes"/> open
/// read-only, and binary content is refused. The limit is configurable; the default is 5 MiB.
/// </summary>
public sealed record EditorLimits
{
    /// <summary>The default open limit, 5 MiB.</summary>
    public const long DefaultMaxOpenBytes = 5 * 1024 * 1024;

    /// <summary>Documents larger than this open read-only, in bytes.</summary>
    public long MaxOpenBytes { get; init; } = DefaultMaxOpenBytes;

    /// <summary>The shipped limits.</summary>
    public static EditorLimits Default { get; } = new();

    /// <summary>True when content counts as binary: it contains a NUL character.</summary>
    public static bool IsBinary(ReadOnlySpan<char> content) => content.Contains('\0');
}
