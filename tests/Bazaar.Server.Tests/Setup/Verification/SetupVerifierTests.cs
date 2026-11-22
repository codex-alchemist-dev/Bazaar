using System.Reflection;
using Bazaar.Admin.Composition;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup.Verification;

/// <summary>The verifier and its rules: overall status, score arithmetic, isolation of a failing check, and the read-only shape.</summary>
public sealed class SetupVerifierTests
{
    /// <summary>A check whose result a test scripts. It can return a result, return null, or throw.</summary>
    private sealed class ScriptedCheck(string id, Func<SetupCheckResult?> run) : ISetupVerificationCheck
    {
        public string Id { get; } = id;

        public string Title => "Scripted " + Id;

        public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken) =>
            Task.FromResult(run()!);
    }

    private static ScriptedCheck Returns(string id, SetupCheckStatus status) =>
        new ScriptedCheck(id, () => new SetupCheckResult(status, $"{id} reports {status}."));

    private static SetupVerificationContext Context() => new(SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!);

    [Fact]
    public void Red_wins_over_amber_and_not_evaluated()
    {
        Assert.Equal(SetupCheckStatus.Red, SetupVerificationRules.Overall([SetupCheckStatus.Green, SetupCheckStatus.Amber, SetupCheckStatus.NotEvaluated, SetupCheckStatus.Red]));
    }

    [Fact]
    public void Amber_and_not_evaluated_can_never_be_green()
    {
        Assert.Equal(SetupCheckStatus.Amber, SetupVerificationRules.Overall([SetupCheckStatus.Green, SetupCheckStatus.Amber]));
        Assert.Equal(SetupCheckStatus.Amber, SetupVerificationRules.Overall([SetupCheckStatus.Green, SetupCheckStatus.NotEvaluated]));
        Assert.Equal(SetupCheckStatus.Amber, SetupVerificationRules.Overall([SetupCheckStatus.NotEvaluated]));
        Assert.Equal(SetupCheckStatus.Green, SetupVerificationRules.Overall([SetupCheckStatus.Green, SetupCheckStatus.Green]));
    }

    [Fact]
    public void Overall_of_no_statuses_is_refused_rather_than_read_as_green()
    {
        Assert.Throws<ArgumentException>(() => SetupVerificationRules.Overall([]));
    }

    [Fact]
    public void Score_is_100_when_every_check_is_green()
    {
        Assert.Equal(100, SetupVerificationRules.Score([SetupCheckStatus.Green, SetupCheckStatus.Green]));
    }

    [Theory]
    [InlineData(new[] { SetupCheckStatus.Red }, 60)]
    [InlineData(new[] { SetupCheckStatus.Amber }, 90)]
    [InlineData(new[] { SetupCheckStatus.NotEvaluated }, 95)]
    [InlineData(new[] { SetupCheckStatus.Red, SetupCheckStatus.Amber, SetupCheckStatus.NotEvaluated }, 45)]
    [InlineData(new[] { SetupCheckStatus.Amber, SetupCheckStatus.Amber }, 80)]
    public void Score_subtracts_each_penalty_once(SetupCheckStatus[] statuses, int expected)
    {
        Assert.Equal(expected, SetupVerificationRules.Score(statuses));
    }

    [Fact]
    public void Red_penalty_is_capped_so_a_third_red_costs_nothing_more()
    {
        Assert.Equal(SetupVerificationRules.RedPenaltyCap, 2 * SetupVerificationRules.RedPenalty);
        Assert.Equal(20, SetupVerificationRules.Score([SetupCheckStatus.Red, SetupCheckStatus.Red]));
        Assert.Equal(20, SetupVerificationRules.Score([SetupCheckStatus.Red, SetupCheckStatus.Red, SetupCheckStatus.Red]));
    }

    [Fact]
    public void Score_is_floored_at_zero()
    {
        var statuses = Enumerable.Repeat(SetupCheckStatus.Amber, 12).ToList();
        Assert.Equal(0, SetupVerificationRules.Score(statuses));
        Assert.Equal(0, SetupVerificationRules.Score([SetupCheckStatus.Red, SetupCheckStatus.Red, SetupCheckStatus.Amber, SetupCheckStatus.Amber, SetupCheckStatus.Amber, SetupCheckStatus.Amber]));
    }

    [Fact]
    public void The_weights_are_the_documented_constants()
    {
        Assert.Equal(40, SetupVerificationRules.RedPenalty);
        Assert.Equal(80, SetupVerificationRules.RedPenaltyCap);
        Assert.Equal(10, SetupVerificationRules.AmberPenalty);
        Assert.Equal(5, SetupVerificationRules.NotEvaluatedPenalty);
        Assert.Equal(100, SetupVerificationRules.MaxScore);
        Assert.Equal(0, SetupVerificationRules.MinScore);
    }

    [Fact]
    public async Task One_red_check_makes_the_report_red_and_keeps_registration_order()
    {
        var verifier = new SetupVerifier([
            Returns("first", SetupCheckStatus.Green),
            Returns("second", SetupCheckStatus.Red),
            Returns("third", SetupCheckStatus.Green),
        ]);

        var report = await verifier.VerifyAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.Red, report.Overall);
        Assert.Equal(60, report.Score);
        Assert.Equal(["first", "second", "third"], report.Checks.Select(row => row.Id));
    }

    [Fact]
    public async Task A_not_evaluated_check_makes_the_overall_amber_and_is_listed_as_an_amber_item()
    {
        var verifier = new SetupVerifier([
            Returns("pure", SetupCheckStatus.Green),
            new NotEvaluatedCheck("infra", "Infra"),
        ]);

        var report = await verifier.VerifyAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.Amber, report.Overall);
        Assert.Equal(95, report.Score);
        Assert.Equal(["infra"], report.AmberItems.Select(item => item.Id));
    }

    [Fact]
    public async Task Amber_items_are_the_amber_and_not_evaluated_checks_only()
    {
        var verifier = new SetupVerifier([
            Returns("green", SetupCheckStatus.Green),
            Returns("amber", SetupCheckStatus.Amber),
            Returns("red", SetupCheckStatus.Red),
            Returns("notEvaluated", SetupCheckStatus.NotEvaluated),
        ]);

        var report = await verifier.VerifyAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(["amber", "notEvaluated"], report.AmberItems.Select(item => item.Id));
    }

    [Fact]
    public async Task A_throwing_check_becomes_red_naming_only_the_exception_type_and_the_others_still_run()
    {
        var ran = new List<string>();
        var verifier = new SetupVerifier([
            new ScriptedCheck("throws", () => throw new InvalidOperationException("canary-secret-0000 leaked here")),
            new ScriptedCheck("after", () =>
            {
                ran.Add("after");
                return SetupCheckResult.Green("after is fine.");
            }),
        ]);

        var report = await verifier.VerifyAsync(Context(), TestContext.Current.CancellationToken);

        var row = report.Checks.Single(check => check.Id == "throws");
        Assert.Equal(SetupCheckStatus.Red, row.Status);
        Assert.Contains("InvalidOperationException", row.Message);
        Assert.DoesNotContain("canary-secret-0000", row.Message);
        Assert.DoesNotContain("leaked", row.Message);
        Assert.Equal(["after"], ran);
        Assert.Equal(SetupCheckStatus.Green, report.Checks.Single(check => check.Id == "after").Status);
    }

    [Fact]
    public async Task A_check_that_returns_no_result_becomes_red()
    {
        var verifier = new SetupVerifier([new ScriptedCheck("silent", () => null)]);

        var report = await verifier.VerifyAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.Red, report.Overall);
        Assert.Contains("returned no result", report.Checks.Single().Message);
    }

    [Fact]
    public async Task A_stop_requested_by_the_caller_is_not_reported_as_a_check_failure()
    {
        var verifier = new SetupVerifier([
            new ScriptedCheck("stop", () => throw new OperationCanceledException()),
        ]);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(Context(), cancellation.Token));
    }

    [Fact]
    public void An_empty_check_set_is_refused_rather_than_read_as_verified()
    {
        Assert.Throws<ArgumentException>(() => new SetupVerifier([]));
    }

    [Fact]
    public void Two_checks_with_one_id_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new SetupVerifier([Returns("same", SetupCheckStatus.Green), Returns("same", SetupCheckStatus.Red)]));
    }

    [Fact]
    public void The_verifier_depends_on_its_checks_and_nothing_else_so_it_cannot_write()
    {
        // A write (an audit row, a step, a store) would need a constructor parameter. The only one is the check list.
        var constructors = typeof(SetupVerifier).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var single = Assert.Single(constructors);
        var parameter = Assert.Single(single.GetParameters());
        Assert.Equal(typeof(IEnumerable<ISetupVerificationCheck>), parameter.ParameterType);
    }

    [Fact]
    public void The_context_refuses_a_missing_plan()
    {
        Assert.Throws<ArgumentNullException>(() => new SetupVerificationContext(null!));
    }

    [Fact]
    public void Setup_completes_only_when_green_or_when_every_amber_item_is_acknowledged()
    {
        var amber = new SetupCheckReport("a", "A", SetupCheckStatus.Amber, "m", null);
        var other = new SetupCheckReport("b", "B", SetupCheckStatus.NotEvaluated, "m", null);
        var red = new SetupCheckReport("r", "R", SetupCheckStatus.Red, "m", null);
        var green = new SetupCheckReport("g", "G", SetupCheckStatus.Green, "m", null);

        var greenReport = new SetupVerificationReport(SetupCheckStatus.Green, 100, [green], []);
        var amberReport = new SetupVerificationReport(SetupCheckStatus.Amber, 80, [green, amber, other], [amber, other]);
        var redReport = new SetupVerificationReport(SetupCheckStatus.Red, 40, [red, amber], [amber]);

        Assert.True(greenReport.CanComplete([]));
        Assert.False(amberReport.CanComplete([]));
        Assert.False(amberReport.CanComplete(["a"]));
        Assert.True(amberReport.CanComplete(["a", "b", "unrelated"]));
        Assert.False(redReport.CanComplete(["a", "r"]));
    }
}
