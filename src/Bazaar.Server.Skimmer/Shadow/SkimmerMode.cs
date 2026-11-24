namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>The value of <c>skimmer.mode</c> (doc 13, "Placement"). New agents start in <see cref="Shadow"/>.</summary>
public enum SkimmerMode
{
    /// <summary>The gate is bypassed: every message wakes.</summary>
    Off,

    /// <summary>Decisions are recorded, delivery is unchanged. Nothing is promoted.</summary>
    Shadow,

    /// <summary>Decisions are enforced. Not built yet: it ships only after the shadow gates pass.</summary>
    Defer,
}
