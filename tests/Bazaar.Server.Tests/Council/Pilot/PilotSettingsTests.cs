using Bazaar.Server.Council.Pilot;

namespace Bazaar.Server.Tests.Council.Pilot;

public class PilotSettingsTests
{
    [Fact]
    public void Defaults_match_the_design_doc_and_are_valid()
    {
        var settings = new PilotSettings();

        Assert.Equal(0.15m, settings.HoldoutShare);
        Assert.Equal(0.20m, settings.StopFlipShare);
        Assert.Equal(20, settings.TargetVotes);
        Assert.Empty(settings.Validate());
    }

    [Theory]
    [InlineData(-0.1, 0.2, 20)]
    [InlineData(1.1, 0.2, 20)]
    [InlineData(0.15, -0.1, 20)]
    [InlineData(0.15, 1.5, 20)]
    [InlineData(0.15, 0.2, 0)]
    public void Out_of_range_values_are_reported(double holdout, double stop, int target)
    {
        var settings = new PilotSettings((decimal)holdout, (decimal)stop, target);

        Assert.NotEmpty(settings.Validate());
    }

    [Fact]
    public void Boundary_values_are_valid()
    {
        Assert.Empty(new PilotSettings(0m, 1m, 1).Validate());
        Assert.Empty(new PilotSettings(1m, 0m, 1).Validate());
    }

    [Fact]
    public void Report_build_rejects_invalid_settings()
    {
        var settings = new PilotSettings(StopFlipShare: 2m);

        Assert.Throws<ArgumentException>(() => PilotReport.Build([], new Dictionary<string, string>(), settings));
    }
}
