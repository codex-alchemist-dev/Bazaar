using Bazaar.Contracts.Soul;

namespace Bazaar.Client.Core.Souls;

/// <summary>
/// The profile card and version history for one Soul, built from a profile and its version chain.
/// Immutable and pure: it does no I/O and takes its data from the caller (or from an
/// <see cref="IVersionHistorySource"/> the caller reads first). Rows run oldest to newest.
/// </summary>
public sealed class SoulProfileViewModel
{
    /// <summary>Number of hash characters shown; the full SHA-256 stays in the version row.</summary>
    public const int ShortHashLength = 12;

    /// <summary>Builds the card.</summary>
    /// <param name="profile">Identity fields for the Soul.</param>
    /// <param name="versions">The Soul's versions, in any order. Empty when the Soul has none yet.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    /// <exception cref="ArgumentException">A version belongs to another Soul, has a version number below 1, or repeats a number.</exception>
    public SoulProfileViewModel(SoulProfile profile, IEnumerable<SoulVersion> versions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(versions);

        var ordered = versions.ToList();
        var seen = new HashSet<int>();
        foreach (var version in ordered)
        {
            if (version.SoulId != profile.SoulId)
            {
                throw new ArgumentException($"Version {version.Version} belongs to another Soul.", nameof(versions));
            }

            if (version.Version < 1)
            {
                throw new ArgumentException($"Version number {version.Version} is below 1.", nameof(versions));
            }

            if (!seen.Add(version.Version))
            {
                throw new ArgumentException($"Version {version.Version} appears more than once.", nameof(versions));
            }
        }

        ordered.Sort((left, right) => left.Version.CompareTo(right.Version));

        SoulId = profile.SoulId;
        Name = profile.Name;
        Role = profile.Role;
        ModelBadge = profile.ModelBadge;
        History = ordered
            .Select(v => new VersionHistoryRow(v.Version, v.ParentVersion, v.Author, v.Reason, v.DecisionRef, v.CreatedAt))
            .ToList();

        var head = ordered.Count > 0 ? ordered[^1] : null;
        CurrentVersion = head?.Version;
        ContentHashShort = head is null
            ? null
            : head.ContentSha256[..Math.Min(ShortHashLength, head.ContentSha256.Length)];
        PreferencesSummary = Souls.PreferencesSummary.Format(head?.PreferencesJson);
    }

    /// <summary>The Soul this card describes.</summary>
    public Guid SoulId { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Role title.</summary>
    public string Role { get; }

    /// <summary>Model badge text; empty when none is set.</summary>
    public string ModelBadge { get; }

    /// <summary>Version number of the head version, or null when the Soul has no versions.</summary>
    public int? CurrentVersion { get; }

    /// <summary>Short form of the head version's content hash, or null when there are no versions.</summary>
    public string? ContentHashShort { get; }

    /// <summary>True once the Soul has been edited, that is, has a version after the first.</summary>
    public bool IsEdited => CurrentVersion is > 1;

    /// <summary>Head version label, such as "v3", or "no version".</summary>
    public string VersionHeadline => CurrentVersion is int version ? "v" + version : "no version";

    /// <summary>One-line summary of the head version's preferences.</summary>
    public string PreferencesSummary { get; }

    /// <summary>The version history, oldest to newest.</summary>
    public IReadOnlyList<VersionHistoryRow> History { get; }

    /// <summary>True when the history has at least one row.</summary>
    public bool HasHistory => History.Count > 0;
}
