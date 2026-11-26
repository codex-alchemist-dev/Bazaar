namespace Bazaar.Server.TimeMachine;

/// <summary>A point in one GC's history: render the GC as it stood at <paramref name="Seq"/> (docs/design/26, section 4).</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Seq">Last seq to include. A value above the viewer's grant is clamped to the grant.</param>
public readonly record struct ReplayCursor(Guid GcId, long Seq);
