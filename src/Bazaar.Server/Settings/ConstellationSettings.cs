namespace Bazaar.Server.Settings;

/// <summary>Git layout mode for the Git Solar System (doc 19 <c>constellation.git_layout</c>).</summary>
public enum ConstellationGitLayout
{
    Deterministic,
    Force,
}

/// <summary>Orbit radius rule for branches (doc 19 <c>constellation.orbit_radius</c>).</summary>
public enum ConstellationOrbitRadius
{
    Age,
    Equal,
}

/// <summary>Where screenshot nodes come from (doc 19 <c>constellation.screenshot_sources</c>).</summary>
public enum ConstellationScreenshotSource
{
    GcAttachments,
    TestArtifacts,
}

/// <summary>
/// The <c>constellation.*</c> settings from doc 19 ("### Settings"). None loosens a visibility or access rule, so the
/// shipped defaults are the only floors. Each change is audited like any other setting.
/// </summary>
public static class ConstellationSettings
{
    public static readonly SettingKey<int> NodeCap = new(
        "constellation.node_cap", 10000, SettingScope.Install, SettingRole.Operator,
        "Above this many nodes the Constellation shows clusters only.",
        validate: Between(1000, 50000));

    public static readonly SettingKey<int> RingDepth = new(
        "constellation.ring_depth", 3, SettingScope.Install, SettingRole.Operator,
        "Number of rings around the focused node in reverse-gravity layout.",
        validate: Between(1, 6));

    public static readonly SettingKey<ConstellationGitLayout> GitLayout = new(
        "constellation.git_layout", ConstellationGitLayout.Deterministic, SettingScope.Project, SettingRole.Owner | SettingRole.Operator,
        "Git Solar System layout: deterministic keeps history from jittering as it grows; force is the physics layout.");

    public static readonly SettingKey<ConstellationOrbitRadius> OrbitRadius = new(
        "constellation.orbit_radius", ConstellationOrbitRadius.Age, SettingScope.Project, SettingRole.Owner | SettingRole.Operator,
        "Orbit radius for branches: age by creation time, or equal radii.");

    public static readonly SettingKey<IReadOnlyList<ConstellationScreenshotSource>> ScreenshotSources = new(
        "constellation.screenshot_sources", [ConstellationScreenshotSource.GcAttachments], SettingScope.Install, SettingRole.Operator,
        "Where screenshot nodes come from: GC attachments, test artifacts, or both. An empty list shows none.",
        validate: CheckNoDuplicates);

    /// <summary>Every constellation key, in doc 19 table order.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        NodeCap,
        RingDepth,
        GitLayout,
        OrbitRadius,
        ScreenshotSources,
    ];

    private static Func<int, string?> Between(int min, int max) =>
        value => value >= min && value <= max ? null : $"must be from {min} to {max}; got {value}.";

    private static string? CheckNoDuplicates(IReadOnlyList<ConstellationScreenshotSource> sources) =>
        sources.Distinct().Count() == sources.Count ? null : "a screenshot source is listed twice.";
}
