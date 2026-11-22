namespace Bazaar.Client.Motion;

/// <summary>What a merge (goo) effect is asked to blend: two small shapes, in the host's coordinates, at a progress from 0 to 1.</summary>
public readonly record struct GooMergeRequest(double FromX, double FromY, double ToX, double ToY, double Progress);

/// <summary>
/// The metaball merge (doc 93, "Implementation [spike, unverified]"). Whether Avalonia 12's composition layer and Skia can run a
/// threshold blur cheaply on Android and Linux is not known, so the effect sits behind this interface. Callers draw the squash
/// and stretch fallback when <see cref="TryApply"/> returns false. The default does nothing (<see cref="NoGooMergeEffect"/>).
/// </summary>
public interface IGooMergeEffect
{
    /// <summary>True while the effect is available and within the frame budget. Hosts drop it first when frames are missed (doc 93 question 8).</summary>
    bool IsAvailable { get; }

    /// <summary>Applies the merge for this frame; false means nothing was drawn and the fallback applies.</summary>
    bool TryApply(in GooMergeRequest request);
}

/// <summary>The default: no merge, so the squash and stretch fallback is always used.</summary>
public sealed class NoGooMergeEffect : IGooMergeEffect
{
    public static NoGooMergeEffect Instance { get; } = new();

    public bool IsAvailable => false;

    public bool TryApply(in GooMergeRequest request) => false;
}
