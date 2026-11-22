using Bazaar.Server.GitSolarSystem.Abstractions;

namespace Bazaar.Server.GitSolarSystem;

/// <summary>
/// The shipped defaults of the solar-system settings, used until the host's settings registry supplies real values
/// (doc 19: deterministic layout, age radius, radius 10 to 100, commit cap 20000).
/// </summary>
public sealed record GitSolarSettings : IGitSolarSettings
{
    public GitSolarLayoutMode LayoutMode { get; init; } = GitSolarLayoutMode.Deterministic;

    public GitSolarOrbitRadiusMode OrbitRadiusMode { get; init; } = GitSolarOrbitRadiusMode.Age;

    public double MinOrbitRadius { get; init; } = 10;

    public double MaxOrbitRadius { get; init; } = 100;

    public int MaxCommits { get; init; } = 20000;
}
