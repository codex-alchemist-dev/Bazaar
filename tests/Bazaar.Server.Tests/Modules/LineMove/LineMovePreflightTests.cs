using Bazaar.Server.Modules.LineMove;

namespace Bazaar.Server.Tests.Modules.LineMove;

/// <summary>The preflight rules as a pure function (doc 42 section 10.3, step 1).</summary>
public sealed class LineMovePreflightTests
{
    private static PreflightCheck CheckFor(PreflightResult result, PreflightRule rule) =>
        Assert.Single(result.Checks, check => check.Rule == rule);

    [Fact]
    public void Healthy_inputs_pass_every_rule()
    {
        var result = LineMovePreflight.Evaluate(LineMoveTestData.HealthyPreflight());

        Assert.True(result.Passed);
        Assert.Equal(4, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.True(check.Passed));
    }

    [Fact]
    public void An_extension_without_a_target_line_artifact_fails_and_is_named()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with
        {
            Extensions =
            [
                new InstalledExtension("pgvector", HasTargetLineArtifact: true),
                new InstalledExtension("pg_search", HasTargetLineArtifact: false),
            ],
        };

        var result = LineMovePreflight.Evaluate(inputs);

        Assert.False(result.Passed);
        var check = CheckFor(result, PreflightRule.ExtensionArtifacts);
        Assert.False(check.Passed);
        Assert.Contains("pg_search", check.Detail);
        Assert.DoesNotContain("pgvector", check.Detail);
    }

    [Fact]
    public void No_installed_extensions_passes_the_artifact_rule()
    {
        var result = LineMovePreflight.Evaluate(LineMoveTestData.HealthyPreflight() with { Extensions = [] });

        Assert.True(CheckFor(result, PreflightRule.ExtensionArtifacts).Passed);
    }

    [Fact]
    public void Disk_exactly_covering_the_snapshot_plus_the_upgrade_passes()
    {
        // Free 80, snapshot 30, upgrade 50: 80 is exactly enough.
        var inputs = LineMoveTestData.HealthyPreflight() with { FreeDiskBytes = 80 };

        Assert.True(CheckFor(LineMovePreflight.Evaluate(inputs), PreflightRule.DiskSpace).Passed);
    }

    [Fact]
    public void Disk_one_byte_short_of_the_snapshot_plus_the_upgrade_fails()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { FreeDiskBytes = 79 };

        Assert.False(CheckFor(LineMovePreflight.Evaluate(inputs), PreflightRule.DiskSpace).Passed);
    }

    [Fact]
    public void Kodo_not_green_on_the_current_line_fails()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { KodoGreenOnCurrentLine = false };

        var check = CheckFor(LineMovePreflight.Evaluate(inputs), PreflightRule.KodoGreen);
        Assert.False(check.Passed);
    }

    [Fact]
    public void A_restore_drill_younger_than_the_maximum_passes()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { RestoreDrillAge = TimeSpan.FromDays(6.9) };

        Assert.True(CheckFor(LineMovePreflight.Evaluate(inputs), PreflightRule.RestoreDrillAge).Passed);
    }

    [Fact]
    public void A_restore_drill_exactly_at_the_maximum_fails_because_the_age_must_be_below_it()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { RestoreDrillAge = TimeSpan.FromDays(7) };

        Assert.False(CheckFor(LineMovePreflight.Evaluate(inputs), PreflightRule.RestoreDrillAge).Passed);
    }

    [Fact]
    public void A_move_with_no_restore_drill_ever_fails_the_drill_rule()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { RestoreDrillAge = null };

        var check = CheckFor(LineMovePreflight.Evaluate(inputs), PreflightRule.RestoreDrillAge);
        Assert.False(check.Passed);
        Assert.Contains("No restore drill", check.Detail);
    }

    [Fact]
    public void Every_failing_rule_is_reported_together()
    {
        var inputs = new LineMovePreflightInputs(
            Extensions: [new InstalledExtension("pgvector", HasTargetLineArtifact: false)],
            FreeDiskBytes: 1,
            SnapshotBytes: 30,
            UpgradeBytes: 50,
            KodoGreenOnCurrentLine: false,
            RestoreDrillAge: null,
            MaxRestoreDrillAge: TimeSpan.FromDays(7));

        var result = LineMovePreflight.Evaluate(inputs);

        Assert.False(result.Passed);
        Assert.Equal(4, result.Checks.Count(check => !check.Passed));
    }

    [Fact]
    public void Negative_byte_counts_are_rejected()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { SnapshotBytes = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => LineMovePreflight.Evaluate(inputs));
    }

    [Fact]
    public void A_negative_maximum_drill_age_is_rejected()
    {
        var inputs = LineMoveTestData.HealthyPreflight() with { MaxRestoreDrillAge = TimeSpan.FromDays(-1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => LineMovePreflight.Evaluate(inputs));
    }
}
