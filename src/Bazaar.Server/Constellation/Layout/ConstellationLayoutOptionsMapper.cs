using Bazaar.Server.Constellation;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// The wiring step between stored <c>constellation.*</c> choices and <see cref="LayoutOptions"/> (docs/design/19, Settings).
/// Pure: it reads its inputs and returns a new value, and never touches storage, DI or the base options.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <c>orbit_radius</c> (<see cref="ConstellationOrbitRadius"/>) sets <see cref="LayoutOptions.OrbitRadiusMode"/>. The radii
/// it scales between (<see cref="LayoutOptions.MinOrbitRadius"/> and <see cref="LayoutOptions.MaxOrbitRadius"/>) are not
/// chosen here. <see cref="LayoutOptions.OrbitSpacing"/> is never changed by this mapping.
/// </description></item>
/// <item><description>
/// <c>git_layout</c> (<see cref="ConstellationGitLayout"/>) is deliberately not mapped onto <see cref="LayoutOptions"/>. It
/// picks the layout engine (<see cref="SolarOrbitLayout"/> or <see cref="ForceLayout"/>), which is a caller dispatch choice,
/// not a tuning field.
/// </description></item>
/// </list>
/// </remarks>
public static class ConstellationLayoutOptionsMapper
{
    /// <summary>Returns a copy of <paramref name="baseOptions"/> with the fields the choices control set. The input is never changed.</summary>
    public static LayoutOptions ToLayoutOptions(ConstellationLayoutChoices choices, LayoutOptions baseOptions)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(baseOptions);

        // git_layout is intentionally unmapped: the caller chooses SolarOrbitLayout or ForceLayout, so no LayoutOptions field carries it.
        return baseOptions with { OrbitRadiusMode = choices.OrbitRadius };
    }
}
