using System.Globalization;
using Bazaar.Server.Modules.Updates;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Updates;

/// <summary>Update policy evaluation (doc 42 section 10.1): the default rows, pin and skip, scope precedence and windows.</summary>
public sealed class UpdatePolicyEvaluatorTests
{
    // Monday 2026-11-16 11:00 UTC. Unless a test says otherwise, this is inside a Monday 10:00-12:00 UTC window.
    private static readonly DateTimeOffset _insideMondayWindow = new(2026, 11, 16, 11, 0, 0, TimeSpan.Zero);

    private static MaintenanceWindow MondayUtcWindow() => Window([DayOfWeek.Monday], "10:00", "12:00");

    private static MaintenanceWindow Window(DayOfWeek[] days, string start, string end, MaintenanceZone? zone = null) =>
        new(days, TimeOnly.Parse(start, CultureInfo.InvariantCulture), TimeOnly.Parse(end, CultureInfo.InvariantCulture), zone ?? MaintenanceZone.Fixed(TimeSpan.Zero));

    private static UpdateCandidate Candidate(
        string version = "18.1.2",
        string line = "18",
        bool isSecurity = true,
        VersionType versionType = VersionType.GeneralAvailability,
        bool matrixPassed = true,
        string moduleId = "postgresql",
        string category = "database",
        string channel = "stable") =>
        new(moduleId, category, line, version, versionType, channel, isSecurity, matrixPassed);

    /// <summary>A context with line 18 installed at 18.1.0, so 18.1.2 is a patch on a line in use.</summary>
    private static UpdateContext Installed18(IReadOnlyList<MaintenanceWindow>? windows = null) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal) { ["18"] = "18.1.0" }, windows ?? []);

    private static UpdatePolicy Policy(
        UpdatePolicyAction action,
        UpdatePolicyScope? scope = null,
        UpdatePolicyConditions? conditions = null,
        IReadOnlyList<MaintenanceWindow>? windows = null) =>
        new(scope ?? new UpdatePolicyScope(), conditions ?? new UpdatePolicyConditions(), windows ?? [], action);

    private static (UpdatePolicyEvaluator Evaluator, FakeTimeProvider Clock) Make(DateTimeOffset? now = null)
    {
        var clock = new FakeTimeProvider(now ?? _insideMondayWindow);
        return (new UpdatePolicyEvaluator(clock), clock);
    }

    private static UpdateEvaluation Evaluate(UpdateCandidate candidate, UpdateContext? context = null, params UpdatePolicy[] policies)
    {
        var (evaluator, _) = Make();
        return evaluator.Evaluate(candidate, context ?? Installed18([MondayUtcWindow()]), policies);
    }

    [Fact]
    public void Security_patch_on_a_line_in_use_is_auto_inside_a_window()
    {
        var result = Evaluate(Candidate());

        Assert.Equal(UpdateDecision.Auto, result.Decision);
        Assert.Equal(UpdateRule.SecurityPatchInWindow, result.Rule);
        Assert.Null(result.NextWindowStart);
    }

    [Fact]
    public void Security_patch_outside_a_window_is_deferred_to_the_next_window()
    {
        var (evaluator, _) = Make(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

        var result = evaluator.Evaluate(Candidate(), Installed18([MondayUtcWindow()]), []);

        Assert.Equal(UpdateDecision.Deferred, result.Decision);
        Assert.Equal(UpdateRule.SecurityPatchDeferred, result.Rule);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), result.NextWindowStart);
    }

    [Fact]
    public void Security_patch_with_no_window_configured_is_deferred_without_a_next_start()
    {
        var (evaluator, _) = Make();

        var result = evaluator.Evaluate(Candidate(), Installed18(), []);

        Assert.Equal(UpdateDecision.Deferred, result.Decision);
        Assert.Equal(UpdateRule.SecurityPatchDeferred, result.Rule);
        Assert.Null(result.NextWindowStart);
    }

    [Fact]
    public void Non_security_patch_on_a_line_in_use_is_manual()
    {
        var result = Evaluate(Candidate(isSecurity: false));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.DefaultManual, result.Rule);
    }

    [Fact]
    public void New_minor_on_the_same_line_is_manual()
    {
        var result = Evaluate(Candidate(version: "18.2.0"));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.DefaultManual, result.Rule);
    }

    [Fact]
    public void Security_minor_on_a_line_in_use_is_manual_because_only_patches_are_automatic()
    {
        var result = Evaluate(Candidate(version: "18.2.0"));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
    }

    [Fact]
    public void New_general_availability_major_line_awaits_owner_approval()
    {
        var result = Evaluate(Candidate(version: "19.0.0", line: "19", isSecurity: false));

        Assert.Equal(UpdateDecision.AwaitingApproval, result.Decision);
        Assert.Equal(UpdateRule.NewLineNeedsApproval, result.Rule);
    }

    [Fact]
    public void New_beta_major_line_is_manual_not_an_approval_request()
    {
        var result = Evaluate(Candidate(version: "19.0.0-beta.1", line: "19", versionType: VersionType.Beta, isSecurity: false));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.DefaultManual, result.Rule);
    }

    [Fact]
    public void Failed_matrix_blocks_a_candidate()
    {
        var result = Evaluate(Candidate(matrixPassed: false));

        Assert.Equal(UpdateDecision.Blocked, result.Decision);
        Assert.Equal(UpdateRule.MatrixFailed, result.Rule);
    }

    [Fact]
    public void Candidate_not_newer_than_the_installed_version_is_skipped()
    {
        var result = Evaluate(Candidate(version: "18.1.0"));

        Assert.Equal(UpdateDecision.Skipped, result.Decision);
        Assert.Equal(UpdateRule.NotNewer, result.Rule);
    }

    [Fact]
    public void Unparseable_version_on_a_line_in_use_falls_back_to_manual_even_for_security()
    {
        var result = Evaluate(Candidate(version: "patch-7"));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.DefaultManual, result.Rule);
    }

    [Fact]
    public void Pin_policy_pins_a_security_patch_that_would_otherwise_be_auto()
    {
        var pin = Policy(UpdatePolicyAction.Pin);

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), pin);

        Assert.Equal(UpdateDecision.Pinned, result.Decision);
        Assert.Equal(UpdateRule.PinnedByPolicy, result.Rule);
        Assert.Same(pin, result.AppliedPolicy);
    }

    [Fact]
    public void Pin_takes_precedence_over_a_failed_matrix()
    {
        var result = Evaluate(Candidate(matrixPassed: false), Installed18(), Policy(UpdatePolicyAction.Pin));

        Assert.Equal(UpdateDecision.Pinned, result.Decision);
    }

    [Fact]
    public void Skip_policy_skips_a_candidate_even_when_it_would_need_approval()
    {
        var result = Evaluate(Candidate(version: "19.0.0", line: "19"), Installed18(), Policy(UpdatePolicyAction.Skip));

        Assert.Equal(UpdateDecision.Skipped, result.Decision);
        Assert.Equal(UpdateRule.SkippedByPolicy, result.Rule);
    }

    [Fact]
    public void Beta_is_never_auto_even_for_a_security_patch_inside_a_window()
    {
        var result = Evaluate(Candidate(versionType: VersionType.Beta, version: "18.1.3-beta.1"));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.PreReleaseNotAuto, result.Rule);
    }

    [Fact]
    public void Release_candidate_is_never_auto_even_when_a_policy_says_auto()
    {
        var auto = Policy(UpdatePolicyAction.Auto);

        var result = Evaluate(Candidate(versionType: VersionType.ReleaseCandidate, version: "18.1.3-rc.1", isSecurity: false), Installed18([MondayUtcWindow()]), auto);

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.PreReleaseNotAuto, result.Rule);
    }

    [Fact]
    public void Window_start_is_inclusive()
    {
        var (evaluator, _) = Make(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero));

        var result = evaluator.Evaluate(Candidate(), Installed18([MondayUtcWindow()]), []);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
    }

    [Fact]
    public void Window_end_is_exclusive()
    {
        var (evaluator, _) = Make(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));

        var result = evaluator.Evaluate(Candidate(), Installed18([MondayUtcWindow()]), []);

        Assert.Equal(UpdateDecision.Deferred, result.Decision);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), result.NextWindowStart);
    }

    [Fact]
    public void Window_in_a_named_zone_uses_that_zones_offset()
    {
        // Berlin is on CET (UTC+1) in mid November, so 02:00 local is 01:00 UTC.
        var berlin = MaintenanceZone.Named(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        var window = Window([DayOfWeek.Monday], "02:00", "04:00", berlin);
        var (evaluator, _) = Make(new DateTimeOffset(2026, 11, 16, 1, 0, 0, TimeSpan.Zero));

        var result = evaluator.Evaluate(Candidate(), Installed18([window]), []);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
    }

    [Fact]
    public void Default_windows_apply_when_the_policy_has_none()
    {
        var sundayOnly = Window([DayOfWeek.Sunday], "10:00", "12:00");

        var result = Evaluate(Candidate(), Installed18([sundayOnly]));

        Assert.Equal(UpdateDecision.Deferred, result.Decision);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero), result.NextWindowStart);
    }

    [Fact]
    public void Policy_windows_replace_the_default_windows()
    {
        var sundayDefault = Window([DayOfWeek.Sunday], "10:00", "12:00");
        var mondayPolicy = Policy(UpdatePolicyAction.Auto, windows: [MondayUtcWindow()]);

        var result = Evaluate(Candidate(), Installed18([sundayDefault]), mondayPolicy);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
        Assert.Equal(UpdateRule.PolicyDecision, result.Rule);
    }

    [Fact]
    public void Policy_auto_on_a_non_security_patch_is_auto_when_no_window_is_configured()
    {
        var result = Evaluate(Candidate(isSecurity: false), Installed18(), Policy(UpdatePolicyAction.Auto));

        Assert.Equal(UpdateDecision.Auto, result.Decision);
        Assert.Equal(UpdateRule.PolicyDecision, result.Rule);
    }

    [Fact]
    public void Policy_auto_outside_its_window_is_deferred()
    {
        var (evaluator, _) = Make(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var auto = Policy(UpdatePolicyAction.Auto, windows: [MondayUtcWindow()]);

        var result = evaluator.Evaluate(Candidate(isSecurity: false), Installed18(), [auto]);

        Assert.Equal(UpdateDecision.Deferred, result.Decision);
        Assert.Equal(UpdateRule.PolicyDecision, result.Rule);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), result.NextWindowStart);
    }

    [Fact]
    public void Policy_defer_reports_the_next_window_start()
    {
        var result = Evaluate(Candidate(isSecurity: false), Installed18([MondayUtcWindow()]), Policy(UpdatePolicyAction.Defer));

        Assert.Equal(UpdateDecision.Deferred, result.Decision);
        Assert.Equal(UpdateRule.PolicyDecision, result.Rule);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), result.NextWindowStart);
    }

    [Fact]
    public void Policy_require_approval_routes_an_in_use_patch_to_the_owner()
    {
        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), Policy(UpdatePolicyAction.RequireApproval));

        Assert.Equal(UpdateDecision.AwaitingApproval, result.Decision);
        Assert.Equal(UpdateRule.PolicyDecision, result.Rule);
    }

    [Fact]
    public void Policy_auto_cannot_adopt_a_new_general_availability_line()
    {
        var result = Evaluate(Candidate(version: "19.0.0", line: "19"), Installed18(), Policy(UpdatePolicyAction.Auto));

        Assert.Equal(UpdateDecision.AwaitingApproval, result.Decision);
        Assert.Equal(UpdateRule.NewLineNeedsApproval, result.Rule);
    }

    [Fact]
    public void Policy_manual_on_a_security_patch_overrides_the_automatic_default()
    {
        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), Policy(UpdatePolicyAction.Manual));

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Equal(UpdateRule.PolicyDecision, result.Rule);
    }

    [Fact]
    public void Module_scope_outranks_a_category_scope()
    {
        var moduleManual = Policy(UpdatePolicyAction.Manual, new UpdatePolicyScope(ModuleId: "postgresql"));
        var categoryAuto = Policy(UpdatePolicyAction.Auto, new UpdatePolicyScope(Category: "database"));

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), categoryAuto, moduleManual);

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Same(moduleManual, result.AppliedPolicy);
    }

    [Fact]
    public void Module_scope_outranks_all_three_less_specific_fields_together()
    {
        var moduleManual = Policy(UpdatePolicyAction.Manual, new UpdatePolicyScope(ModuleId: "postgresql"));
        var threeFields = Policy(
            UpdatePolicyAction.Auto,
            new UpdatePolicyScope(Category: "database", VersionType: VersionType.GeneralAvailability, Channel: "stable"));

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), threeFields, moduleManual);

        Assert.Equal(UpdateDecision.Manual, result.Decision);
        Assert.Same(moduleManual, result.AppliedPolicy);
    }

    [Fact]
    public void Category_scope_outranks_version_type_and_channel_together()
    {
        var categoryManual = Policy(UpdatePolicyAction.Manual, new UpdatePolicyScope(Category: "database"));
        var typeAndChannel = Policy(
            UpdatePolicyAction.Auto,
            new UpdatePolicyScope(VersionType: VersionType.GeneralAvailability, Channel: "stable"));

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), typeAndChannel, categoryManual);

        Assert.Equal(UpdateDecision.Manual, result.Decision);
    }

    [Fact]
    public void Version_type_scope_outranks_channel_scope()
    {
        var typeAuto = Policy(UpdatePolicyAction.Auto, new UpdatePolicyScope(VersionType: VersionType.GeneralAvailability));
        var channelManual = Policy(UpdatePolicyAction.Manual, new UpdatePolicyScope(Channel: "stable"));

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), channelManual, typeAuto);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
        Assert.Same(typeAuto, result.AppliedPolicy);
    }

    [Fact]
    public void Policy_for_another_module_does_not_apply()
    {
        var otherModule = Policy(UpdatePolicyAction.Manual, new UpdatePolicyScope(ModuleId: "pgvector"));

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), otherModule);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
        Assert.Null(result.AppliedPolicy);
    }

    [Fact]
    public void Policy_for_another_channel_does_not_apply()
    {
        var beta = Policy(UpdatePolicyAction.Pin, new UpdatePolicyScope(Channel: "beta"));

        var result = Evaluate(Candidate(channel: "stable"), Installed18([MondayUtcWindow()]), beta);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
    }

    [Fact]
    public void Conditions_limit_which_candidates_a_policy_touches()
    {
        var nonSecurityOnlyPin = Policy(UpdatePolicyAction.Pin, conditions: new UpdatePolicyConditions(IsSecurity: false));

        var securityResult = Evaluate(Candidate(isSecurity: true), Installed18([MondayUtcWindow()]), nonSecurityOnlyPin);
        var nonSecurityResult = Evaluate(Candidate(isSecurity: false), Installed18([MondayUtcWindow()]), nonSecurityOnlyPin);

        Assert.Equal(UpdateDecision.Auto, securityResult.Decision);
        Assert.Equal(UpdateDecision.Pinned, nonSecurityResult.Decision);
    }

    [Fact]
    public void Matrix_condition_limits_a_policy_to_candidates_with_the_matching_result()
    {
        var matrixFailedOnly = Policy(UpdatePolicyAction.Manual, conditions: new UpdatePolicyConditions(MatrixPassed: false));

        var result = Evaluate(Candidate(matrixPassed: true), Installed18([MondayUtcWindow()]), matrixFailedOnly);

        Assert.Equal(UpdateDecision.Auto, result.Decision);
    }

    [Fact]
    public void Equal_specificity_ties_go_to_the_policy_listed_first()
    {
        var first = Policy(UpdatePolicyAction.Manual, new UpdatePolicyScope(Category: "database"));
        var second = Policy(UpdatePolicyAction.Auto, new UpdatePolicyScope(Category: "database"));

        var result = Evaluate(Candidate(), Installed18([MondayUtcWindow()]), first, second);

        Assert.Same(first, result.AppliedPolicy);
        Assert.Equal(UpdateDecision.Manual, result.Decision);
    }

    [Fact]
    public void Evaluation_uses_the_injected_clock()
    {
        var (evaluator, clock) = Make(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero));
        var context = Installed18([MondayUtcWindow()]);

        Assert.Equal(UpdateDecision.Deferred, evaluator.Evaluate(Candidate(), context, []).Decision);

        clock.SetUtcNow(new DateTimeOffset(2026, 11, 16, 10, 30, 0, TimeSpan.Zero));

        Assert.Equal(UpdateDecision.Auto, evaluator.Evaluate(Candidate(), context, []).Decision);
    }
}
