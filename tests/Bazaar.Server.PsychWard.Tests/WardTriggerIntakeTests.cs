using Bazaar.Sanitization;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.PsychWard.Tests;

public class WardTriggerIntakeTests
{
    private static readonly Guid _a = Guid.Parse("00000000-0000-7000-8000-00000000000a");

    private sealed class Passthrough : ITextSanitizer
    {
        public SanitizedText Sanitize(string text, SensitiveTerms terms) => new(text, []);
    }

    private static ServiceProvider Host(PsychWardSettings? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITextSanitizer, Passthrough>();
        if (settings is not null)
        {
            services.AddSingleton(settings);
        }

        services.AddBazaarPsychWard();
        return services.BuildServiceProvider();
    }

    private static Task<IReadOnlyList<WardStay>> Active(ServiceProvider sp) =>
        sp.GetRequiredService<IWardStayStore>().ListActiveAsync(CancellationToken.None);

    [Fact]
    public async Task Three_consecutive_failures_on_a_tag_admit_with_repeated_failure()
    {
        using var sp = Host();
        var intake = sp.GetRequiredService<IWardTriggerIntake>();

        var outcome = await intake.ReportOutcomesAsync(_a, "csharp", [true, true, true, false], CancellationToken.None);

        Assert.True(outcome!.Accepted);
        var stay = Assert.Single(await Active(sp));
        Assert.Equal(WardTrigger.RepeatedFailure, stay.Trigger);
        Assert.Contains("csharp", stay.Detail);
    }

    [Theory]
    [InlineData(new[] { true, true })]
    [InlineData(new[] { true, false, true })]
    [InlineData(new[] { false, true, true, true })]
    public async Task A_short_window_or_a_success_among_the_newest_three_does_not_admit(bool[] window)
    {
        using var sp = Host();

        var outcome = await sp.GetRequiredService<IWardTriggerIntake>().ReportOutcomesAsync(_a, "csharp", window, CancellationToken.None);

        Assert.Null(outcome);
        Assert.Empty(await Active(sp));
    }

    [Fact]
    public async Task The_failure_count_is_a_setting()
    {
        using var sp = Host(new PsychWardSettings { RepeatedFailureCount = 2 });

        var outcome = await sp.GetRequiredService<IWardTriggerIntake>().ReportOutcomesAsync(_a, "t", [true, true], CancellationToken.None);

        Assert.True(outcome!.Accepted);
    }

    [Fact]
    public async Task A_canary_use_admits_at_once_and_keeps_the_detail_on_one_short_line()
    {
        using var sp = Host();

        await sp.GetRequiredService<IWardTriggerIntake>().ReportCanaryUseAsync(_a, "file read\nsecond line " + new string('x', 500), CancellationToken.None);

        var stay = Assert.Single(await Active(sp));
        Assert.Equal(WardTrigger.DeniedCallOrCanary, stay.Trigger);
        Assert.DoesNotContain('\n', stay.Detail!);
        Assert.True(stay.Detail!.Length <= "canary use: ".Length + 200);
    }

    [Fact]
    public async Task Denied_calls_admit_only_at_the_threshold()
    {
        using var sp = Host();
        var intake = sp.GetRequiredService<IWardTriggerIntake>();

        Assert.Null(await intake.ReportDeniedCallsAsync(_a, 4, "tools", CancellationToken.None));
        Assert.Empty(await Active(sp));
        Assert.True((await intake.ReportDeniedCallsAsync(_a, 5, "tools", CancellationToken.None))!.Accepted);
        Assert.Single(await Active(sp));
    }

    [Fact]
    public async Task A_token_hard_stop_admits_with_its_trigger()
    {
        using var sp = Host();

        await sp.GetRequiredService<IWardTriggerIntake>().ReportTokenHardStopAsync(_a, "daily_tokens", CancellationToken.None);

        Assert.Equal(WardTrigger.TokenHardStop, Assert.Single(await Active(sp)).Trigger);
    }

    [Fact]
    public async Task Owner_only_mode_refuses_every_trigger_except_the_owner_referral()
    {
        using var sp = Host(new PsychWardSettings { AdmissionMode = WardAdmissionMode.OwnerOnly });
        var intake = sp.GetRequiredService<IWardTriggerIntake>();

        Assert.False((await intake.SelfReferAsync(_a, "stuck", CancellationToken.None)).Accepted);
        Assert.False((await intake.ReportTokenHardStopAsync(_a, "x", CancellationToken.None)).Accepted);
        Assert.False((await intake.ReportCanaryUseAsync(_a, "x", CancellationToken.None)).Accepted);
        Assert.Empty(await Active(sp));

        Assert.True((await intake.OwnerReferAsync(_a, "please rest", CancellationToken.None)).Accepted);
        Assert.Equal(WardTrigger.OwnerReferral, Assert.Single(await Active(sp)).Trigger);
    }

    [Fact]
    public async Task A_second_trigger_for_an_admitted_agent_opens_no_second_stay()
    {
        using var sp = Host();
        var intake = sp.GetRequiredService<IWardTriggerIntake>();

        await intake.SelfReferAsync(_a, "stuck", CancellationToken.None);
        var second = await intake.ReportTokenHardStopAsync(_a, "x", CancellationToken.None);

        Assert.False(second.Accepted);
        Assert.Single(await Active(sp));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    public void A_count_below_one_is_rejected(int failures, int denied)
    {
        using var sp = Host(new PsychWardSettings { RepeatedFailureCount = failures == 0 ? 0 : 3, DeniedCallThreshold = denied == 0 ? 0 : 5 });

        Assert.Throws<ArgumentOutOfRangeException>(() => sp.GetRequiredService<IPsychWardService>());
    }
}
