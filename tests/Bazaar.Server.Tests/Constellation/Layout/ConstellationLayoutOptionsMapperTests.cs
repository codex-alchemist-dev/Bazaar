using Bazaar.Server.Constellation;
using Bazaar.Server.Constellation.Layout;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Constellation.Layout;

public class ConstellationLayoutOptionsMapperTests
{
    private static ConstellationLayoutChoices Choices(ConstellationOrbitRadius orbitRadius = ConstellationOrbitRadius.Age, ConstellationGitLayout gitLayout = ConstellationGitLayout.Deterministic) =>
        new(orbitRadius, gitLayout);

    [Fact]
    public void Base_options_are_not_mutated()
    {
        var baseOptions = new LayoutOptions { Seed = 42, OrbitSpacing = 7, Gravity = 3 };

        _ = ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(ConstellationOrbitRadius.Equal, ConstellationGitLayout.Force), baseOptions);

        Assert.Equal(42, baseOptions.Seed);
        Assert.Equal(7, baseOptions.OrbitSpacing);
        Assert.Equal(3, baseOptions.Gravity);
    }

    [Fact]
    public void Result_is_a_new_instance_not_the_base_instance()
    {
        var baseOptions = new LayoutOptions();

        var result = ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(), baseOptions);

        Assert.NotSame(baseOptions, result);
    }

    [Fact]
    public void Default_choices_produce_the_same_options_as_the_base()
    {
        var baseOptions = new LayoutOptions();

        var result = ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(), baseOptions);

        Assert.Equal(baseOptions, result);
    }

    [Theory]
    [InlineData(ConstellationOrbitRadius.Age)]
    [InlineData(ConstellationOrbitRadius.Equal)]
    public void Orbit_radius_choice_is_not_mapped_onto_orbit_spacing(ConstellationOrbitRadius orbitRadius)
    {
        // Neither enum value carries a numeric spacing; see the mapper remarks. Pinned so a later mapping is a deliberate change.
        var baseOptions = new LayoutOptions { OrbitSpacing = 7 };

        var result = ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(orbitRadius: orbitRadius), baseOptions);

        Assert.Equal(7, result.OrbitSpacing);
    }

    [Theory]
    [InlineData(ConstellationGitLayout.Deterministic)]
    [InlineData(ConstellationGitLayout.Force)]
    public void Git_layout_choice_does_not_change_any_layout_option(ConstellationGitLayout gitLayout)
    {
        // The layout engine is chosen by the caller, not by a LayoutOptions field; see the mapper remarks.
        var baseOptions = new LayoutOptions { Seed = 42, OrbitSpacing = 7, ScalingRatio = 12, Theta = 0.5 };

        var result = ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(gitLayout: gitLayout), baseOptions);

        Assert.Equal(baseOptions, result);
    }

    [Fact]
    public void Every_choice_combination_keeps_all_base_fields()
    {
        var baseOptions = new LayoutOptions { Seed = 9, Dimensions = 3, InitialSpread = 4, JitterTolerance = 2, MaxDisplacement = 6, OrbitSpacing = 5 };

        foreach (var orbitRadius in Enum.GetValues<ConstellationOrbitRadius>())
        {
            foreach (var gitLayout in Enum.GetValues<ConstellationGitLayout>())
            {
                var result = ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(orbitRadius, gitLayout), baseOptions);

                Assert.Equal(baseOptions, result);
            }
        }
    }

    [Fact]
    public void Null_choices_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ConstellationLayoutOptionsMapper.ToLayoutOptions(null!, new LayoutOptions()));
    }

    [Fact]
    public void Null_base_options_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => ConstellationLayoutOptionsMapper.ToLayoutOptions(Choices(), null!));
    }
}
