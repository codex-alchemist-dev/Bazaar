using System.Globalization;
using Bazaar.OfflineSync;

namespace Bazaar.Server.OfflineSync.Rules;

/// <summary>A human review item opened by a merge rule (doc 52 section 3, doc 49).</summary>
/// <param name="Kind">Short kind name, for example "kanban-field-conflict".</param>
/// <param name="Target">The object key the write was for.</param>
/// <param name="WriteId">The write that needs a human decision.</param>
/// <param name="BaseVersion">The base version the write named, if any.</param>
/// <param name="ServerVersion">The server version when the decision was made.</param>
/// <param name="Reason">Why a human must decide.</param>
public sealed record ReviewItem(string Kind, string Target, Guid WriteId, string? BaseVersion, string? ServerVersion, string Reason);

/// <summary>Receives review items opened by merge rules.</summary>
public interface IReviewSink
{
    /// <summary>Opens a review item.</summary>
    ValueTask OpenAsync(ReviewItem item, CancellationToken cancellationToken);
}

/// <summary>Version helpers shared by the rules. Versions are decimal sequence numbers as text.</summary>
public static class RuleVersions
{
    /// <summary>Formats a version number.</summary>
    /// <param name="version">The version number.</param>
    /// <returns>The invariant decimal text.</returns>
    public static string Format(long version) => version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses a version written by <see cref="Format"/>.</summary>
    /// <param name="version">The text, possibly null.</param>
    /// <returns>The version number, or null when absent or malformed.</returns>
    public static long? Parse(string? version)
    {
        if (long.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0)
        {
            return value;
        }

        return null;
    }
}

/// <summary>Builds the common outcomes returned by the rules.</summary>
public static class RuleOutcomes
{
    /// <summary>A write that was applied.</summary>
    /// <param name="writeId">The write.</param>
    /// <param name="serverVersion">The server version after the merge.</param>
    /// <returns>The outcome.</returns>
    public static OfflineWriteOutcome Applied(Guid writeId, string? serverVersion = null) =>
        new(writeId, OfflineOutcomeKind.Applied, serverVersion);

    /// <summary>A write whose id was already seen.</summary>
    /// <param name="writeId">The write.</param>
    /// <returns>The outcome.</returns>
    public static OfflineWriteOutcome Duplicate(Guid writeId) =>
        new(writeId, OfflineOutcomeKind.Duplicate);

    /// <summary>A write that was refused.</summary>
    /// <param name="writeId">The write.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>The outcome.</returns>
    public static OfflineWriteOutcome Rejected(Guid writeId, string reason) =>
        new(writeId, OfflineOutcomeKind.Rejected, null, reason);
}
