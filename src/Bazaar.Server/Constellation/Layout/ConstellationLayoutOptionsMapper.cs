using Bazaar.Server.Constellation;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Constellation.Layout;

/// <summary>
/// The wiring step between stored <c>constellation.*</c> choices and <see cref="LayoutOptions"/> (docs/design/19, Settings).
/// Pure: it reads its inputs and returns a new value, and never touches storage, DI or the base options.
/// </summary>
/// <remarks>
/// Neither choice maps to a <see cref="LayoutOptions"/> field yet, so the result equals <paramref name="baseOptions"/>.
/// <list type="bullet">
/// <item><description>
/// <c>orbit_radius</c> (<see cref="ConstellationOrbitRadius"/>) is an enum with no numeric meaning. Design doc 19 does not
/// say what <c>age</c> or <c>equal</c> means as a ring spacing, so it is not mapped onto <see cref="LayoutOptions.OrbitSpacing"/>.
/// </description></item>
/// <item><description>
/// <c>git_layout</c> (<see cref="ConstellationGitLayout"/>) picks the layout engine (<see cref="SolarOrbitLayout"/> or
/// <see cref="ForceLayout"/>). It is a caller dispatch choice, not a tuning field, so it is not mapped either.
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

        return baseOptions with { };
    }
}
