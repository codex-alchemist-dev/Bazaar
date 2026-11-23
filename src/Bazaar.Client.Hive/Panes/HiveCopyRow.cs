namespace Bazaar.Client.Hive.Panes;

/// <summary>The lifecycle state of one Hive copy. <c>inReview</c> copies wait for the review window to end.</summary>
public enum HiveCopyState
{
    InReview,
    Active,
    Deprecated,
}

/// <summary>
/// One copy row. <see cref="Line"/> is the label the view shows: origin project, origin kind, trust and state. A deprecated
/// copy stays in the list and carries a visible marker; it is never hidden.
/// </summary>
public sealed record HiveCopyRow(
    string Path,
    string OriginProject,
    string OriginKind,
    string Trust,
    HiveCopyState State,
    string? ReviewUntil)
{
    public bool IsDeprecated => State == HiveCopyState.Deprecated;

    public string StateText => State switch
    {
        HiveCopyState.Active => "active",
        HiveCopyState.Deprecated => "deprecated",
        _ => $"in review until {ReviewUntil}",
    };

    public string Line =>
        (IsDeprecated ? "[deprecated] " : string.Empty)
        + $"{Path} · origin {OriginProject} · {OriginKind} · trust {Trust} · {StateText}";
}
