namespace Bazaar.Client.Core.Mobile;

public enum PushProviderKind
{
    UnifiedPush,
    Fcm,
}

/// <summary>
/// The phone's install-scope settings with the defaults from doc 29. Settings tighten floors and never loosen them:
/// a fresh biometric approval for merges is not a setting, so it is always required.
/// </summary>
public sealed record AndroidSettings
{
    public static IReadOnlyList<string> DefaultRadialSectors { get; } =
    [
        "Reply",
        "Approvals",
        "Kanban",
        "Agents",
        "Annotate",
        "Sketch",
        "Start screen",
    ];

    public IReadOnlyList<string> RadialSectors { get; init; } = DefaultRadialSectors;

    public HalfOpenLayoutPolicy HalfOpenLayout { get; init; } = HalfOpenLayoutPolicy.UnfoldedIfWide;

    public bool FoldedFullDiff { get; init; }

    public bool MergeFromFolded { get; init; } = true;

    public PushProviderKind PushProvider { get; init; } = PushProviderKind.UnifiedPush;

    public bool VoiceKeepAudio { get; init; }

    /// <summary>Merges always need a fresh biometric approval (doc 29, merge_from_folded). Not configurable.</summary>
    public static bool RequiresFreshBiometricForMerge => true;

    /// <summary>Problems with these settings, or an empty list when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (RadialSectors.Count == 0)
        {
            problems.Add("android.radial_sectors needs at least one sector.");
        }

        if (RadialSectors.Distinct(StringComparer.Ordinal).Count() != RadialSectors.Count)
        {
            problems.Add("android.radial_sectors must not repeat a sector.");
        }

        if (RadialSectors.Any(string.IsNullOrWhiteSpace))
        {
            problems.Add("android.radial_sectors must not contain a blank sector.");
        }

        return problems;
    }
}
