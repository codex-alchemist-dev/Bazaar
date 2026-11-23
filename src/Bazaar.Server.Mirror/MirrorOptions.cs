namespace Bazaar.Server.Mirror;

/// <summary>The Mirror settings from doc 26 (<c>Bazaar:Mirror:*</c>). Defaults are the safe ones.</summary>
public sealed record MirrorOptions
{
    public IReadOnlySet<MirrorSignalKind> SignalKinds { get; init; } = new HashSet<MirrorSignalKind>(Enum.GetValues<MirrorSignalKind>());
    public int EvidenceThreshold { get; init; } = 5;
    public bool AllowRewriteOffers { get; init; } = true;
    public MirrorModelSource ModelSource { get; init; } = MirrorModelSource.Local;
}
