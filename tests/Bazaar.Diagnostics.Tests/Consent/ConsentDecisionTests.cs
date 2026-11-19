using Bazaar.Diagnostics.Consent;

namespace Bazaar.Diagnostics.Tests.Consent;

public class ConsentDecisionTests
{
    private static ConsentSettings In(ConsentMode mode) => new() { Mode = mode };

    [Theory]
    [InlineData(ConsentMode.Off, false, ConsentAction.SendNothing)]
    [InlineData(ConsentMode.Off, true, ConsentAction.SendNothing)]
    [InlineData(ConsentMode.Ask, false, ConsentAction.SendNothing)]
    [InlineData(ConsentMode.Ask, true, ConsentAction.PromptPerson)]
    [InlineData(ConsentMode.Auto, false, ConsentAction.SendNothing)]
    [InlineData(ConsentMode.Auto, true, ConsentAction.SendAutomatically)]
    public void Every_mode_with_and_without_a_report(ConsentMode mode, bool reportExists, ConsentAction expected)
    {
        Assert.Equal(expected, ConsentDecision.Decide(In(mode), reportExists));
    }

    [Fact]
    public void Default_settings_prompt_the_person_when_a_report_exists()
    {
        Assert.Equal(ConsentAction.PromptPerson, ConsentDecision.Decide(ConsentSettings.Default, crashReportExists: true));
    }

    [Fact]
    public void Off_sends_nothing_even_with_auto_style_settings_elsewhere()
    {
        var settings = new ConsentSettings { Mode = ConsentMode.Off, Screenshot = ScreenshotMode.Allowed, NpuDiagnosis = true };

        Assert.Equal(ConsentAction.SendNothing, ConsentDecision.Decide(settings, crashReportExists: true));
    }

    [Fact]
    public void Unknown_mode_value_fails_safe_to_send_nothing()
    {
        var settings = new ConsentSettings { Mode = (ConsentMode)99 };

        Assert.Equal(ConsentAction.SendNothing, ConsentDecision.Decide(settings, crashReportExists: true));
    }

    [Fact]
    public void Null_settings_are_rejected_with_an_exception()
    {
        Assert.Throws<ArgumentNullException>(() => ConsentDecision.Decide(null!, crashReportExists: true));
    }
}
