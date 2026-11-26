namespace Bazaar.Server.Portability;

/// <summary>
/// Size and shape limits applied while an import source is read (doc 44 section 8.3). Operators tighten them
/// through the settings of doc 44 section 12. The expansion ratio has a floor, so it can be lowered only to
/// <see cref="MinExpansionRatio"/>. Values that do not apply to a source type are ignored by its importer.
/// </summary>
public sealed record ImportLimits
{
    /// <summary>Default upload cap: <c>portability.import.max_upload_bytes</c>, 10 GiB.</summary>
    public const long DefaultMaxSourceBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>Default compressed-content expansion cap: <c>portability.import.max_expansion_ratio</c>, 100:1.</summary>
    public const int DefaultMaxExpansionRatio = 100;

    /// <summary>Floor for the expansion ratio setting.</summary>
    public const int MinExpansionRatio = 10;

    /// <summary>Default maximum number of entries in one archive.</summary>
    public const int DefaultMaxArchiveEntries = 10_000;

    /// <summary>Default maximum XML nesting depth.</summary>
    public const int DefaultMaxNestingDepth = 100;

    /// <summary>Default maximum size of one record's body, in UTF-8 bytes (64 MiB).</summary>
    public const int DefaultMaxRecordBytes = 64 * 1024 * 1024;

    /// <summary>Default maximum bytes read out of one archive after decompression (512 MiB).</summary>
    public const long DefaultMaxExpandedBytes = 512L * 1024 * 1024;

    public ImportLimits(
        long maxSourceBytes = DefaultMaxSourceBytes,
        int maxExpansionRatio = DefaultMaxExpansionRatio,
        int maxArchiveEntries = DefaultMaxArchiveEntries,
        int maxNestingDepth = DefaultMaxNestingDepth,
        int maxRecordBytes = DefaultMaxRecordBytes,
        long maxExpandedBytes = DefaultMaxExpandedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSourceBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExpansionRatio, MinExpansionRatio);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxArchiveEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNestingDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecordBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExpandedBytes, 1);

        MaxSourceBytes = maxSourceBytes;
        MaxExpansionRatio = maxExpansionRatio;
        MaxArchiveEntries = maxArchiveEntries;
        MaxNestingDepth = maxNestingDepth;
        MaxRecordBytes = maxRecordBytes;
        MaxExpandedBytes = maxExpandedBytes;
    }

    /// <summary>The shipped defaults.</summary>
    public static ImportLimits Default { get; } = new();

    public long MaxSourceBytes { get; }
    public int MaxExpansionRatio { get; }
    public int MaxArchiveEntries { get; }
    public int MaxNestingDepth { get; }
    public int MaxRecordBytes { get; }
    public long MaxExpandedBytes { get; }
}

/// <summary>A source broke an import limit or an archive safety rule. The import fails; nothing is written.</summary>
public sealed class ImportLimitException : Exception
{
    public ImportLimitException()
    {
    }

    public ImportLimitException(string message) : base(message)
    {
    }

    public ImportLimitException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
