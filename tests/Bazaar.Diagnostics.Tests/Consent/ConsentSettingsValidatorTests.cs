using Bazaar.Diagnostics.Consent;

namespace Bazaar.Diagnostics.Tests.Consent;

public class ConsentSettingsValidatorTests
{
    private const string _model = "diag-model-small";

    private static ConsentValidationContext Context(bool coworker = true, params string[] models) =>
        new(coworker, models.Length == 0 ? [_model] : models);

    /// <summary>Defaults with the diagnosis model resolved, so the baseline is valid.</summary>
    private static ConsentSettings Resolved() => ConsentSettings.Default with { DiagnosisModel = _model };

    private static ConsentValidationResult Validate(ConsentSettings proposed, ConsentSettings? current = null, ConsentValidationContext? context = null) =>
        ConsentSettingsValidator.Validate(proposed, current, context ?? Context());

    [Fact]
    public void Defaults_with_a_resolved_model_are_valid_and_need_no_approval()
    {
        var result = Validate(Resolved());

        Assert.True(result.IsValid);
        Assert.Empty(result.Problems);
        Assert.Empty(result.RequiresOwnerApproval);
    }

    [Fact]
    public void Default_model_is_unresolved_and_refused_while_diagnosis_is_on()
    {
        var result = Validate(ConsentSettings.Default);

        Assert.False(result.IsValid);
        var problem = Assert.Single(result.Problems);
        Assert.Contains(ConsentSettingKeys.DiagnosisModel, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_model_is_accepted_when_diagnosis_is_off()
    {
        var result = Validate(ConsentSettings.Default with { Diagnosis = ConsentMode.Off });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Model_outside_the_whitelist_is_refused()
    {
        var result = Validate(Resolved() with { DiagnosisModel = "not-whitelisted" }, context: Context());

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("not-whitelisted", StringComparison.Ordinal));
    }

    [Fact]
    public void Model_check_is_ordinal()
    {
        var result = Validate(Resolved() with { DiagnosisModel = _model.ToUpperInvariant() });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Auto_with_screenshot_ask_is_refused()
    {
        var result = Validate(Resolved() with { Mode = ConsentMode.Auto, Screenshot = ScreenshotMode.Ask });

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("no person is present", StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_with_screenshot_allowed_or_off_is_not_refused_by_that_rule()
    {
        var allowed = Validate(Resolved() with { Mode = ConsentMode.Auto, Screenshot = ScreenshotMode.Allowed }, current: Resolved() with { Mode = ConsentMode.Auto });
        var off = Validate(Resolved() with { Mode = ConsentMode.Auto, Screenshot = ScreenshotMode.Off }, current: Resolved() with { Mode = ConsentMode.Auto });

        Assert.True(allowed.IsValid);
        Assert.True(off.IsValid);
    }

    [Fact]
    public void Issue_repo_with_mode_off_is_refused()
    {
        var result = Validate(Resolved() with { Mode = ConsentMode.Off, IssueRepo = "owner/name" });

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("must be empty when", StringComparison.Ordinal));
    }

    [Fact]
    public void Issue_repo_with_mode_ask_or_auto_is_allowed()
    {
        var ask = Validate(Resolved() with { IssueRepo = "owner/name" }, current: Resolved());
        var auto = Validate(Resolved() with { Mode = ConsentMode.Auto, IssueRepo = "owner/name" }, current: Resolved());

        Assert.True(ask.IsValid);
        Assert.True(auto.IsValid);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("owner/name/extra")]
    [InlineData("/name")]
    [InlineData("owner/")]
    [InlineData("own er/name")]
    [InlineData("owner/name ")]
    public void Malformed_issue_repo_is_refused(string repo)
    {
        var result = Validate(Resolved() with { IssueRepo = repo });

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.StartsWith(ConsentSettingKeys.IssueRepo, StringComparison.Ordinal));
    }

    [Fact]
    public void Issue_repo_part_longer_than_100_is_refused()
    {
        var longName = new string('a', 101);

        var result = Validate(Resolved() with { IssueRepo = $"owner/{longName}" });

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Issue_repo_part_of_100_characters_is_accepted()
    {
        var name = new string('a', 100);

        var result = Validate(Resolved() with { IssueRepo = $"owner/{name}" }, current: Resolved());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Screenshot_retention_above_retention_is_refused()
    {
        var result = Validate(Resolved() with { RetentionDays = 20, ScreenshotRetentionDays = 30 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.StartsWith(ConsentSettingKeys.ScreenshotRetentionDays, StringComparison.Ordinal));
    }

    [Fact]
    public void Screenshot_retention_equal_to_retention_is_accepted()
    {
        var result = Validate(Resolved() with { RetentionDays = 30, ScreenshotRetentionDays = 30 });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Npu_diagnosis_without_coworker_module_is_refused()
    {
        var result = Validate(Resolved() with { NpuDiagnosis = true }, current: Resolved(), context: Context(coworker: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.Contains("coworker module", StringComparison.Ordinal));
    }

    [Fact]
    public void Npu_diagnosis_with_coworker_module_is_accepted_but_needs_approval()
    {
        var result = Validate(Resolved() with { NpuDiagnosis = true }, current: Resolved(), context: Context(coworker: true));

        Assert.True(result.IsValid);
        Assert.Equal([ConsentSettingKeys.NpuDiagnosis], result.RequiresOwnerApproval);
    }

    [Theory]
    [InlineData(ConsentSettingKeys.LogLines, 0, true)]
    [InlineData(ConsentSettingKeys.LogLines, 500, true)]
    [InlineData(ConsentSettingKeys.LogLines, -1, false)]
    [InlineData(ConsentSettingKeys.LogLines, 501, false)]
    [InlineData(ConsentSettingKeys.RetentionDays, 1, true)]
    [InlineData(ConsentSettingKeys.RetentionDays, 0, false)]
    [InlineData(ConsentSettingKeys.RetentionDays, 365, true)]
    [InlineData(ConsentSettingKeys.RetentionDays, 366, false)]
    [InlineData(ConsentSettingKeys.ScreenshotRetentionDays, 90, true)]
    [InlineData(ConsentSettingKeys.ScreenshotRetentionDays, 91, false)]
    [InlineData(ConsentSettingKeys.LocalQueueDays, 1, true)]
    [InlineData(ConsentSettingKeys.LocalQueueDays, 30, true)]
    [InlineData(ConsentSettingKeys.LocalQueueDays, 31, false)]
    [InlineData(ConsentSettingKeys.IssuesPerDay, 0, true)]
    [InlineData(ConsentSettingKeys.IssuesPerDay, 200, true)]
    [InlineData(ConsentSettingKeys.IssuesPerDay, 201, false)]
    public void Integer_ranges_are_enforced_at_both_ends(string key, int value, bool valid)
    {
        var proposed = key switch
        {
            ConsentSettingKeys.LogLines => Resolved() with { LogLines = value },
            ConsentSettingKeys.RetentionDays => Resolved() with { RetentionDays = value, ScreenshotRetentionDays = Math.Min(30, value) },
            ConsentSettingKeys.ScreenshotRetentionDays => Resolved() with { RetentionDays = 90, ScreenshotRetentionDays = value },
            ConsentSettingKeys.LocalQueueDays => Resolved() with { LocalQueueDays = value },
            ConsentSettingKeys.IssuesPerDay => Resolved() with { IssuesPerDay = value },
            _ => throw new InvalidOperationException(key),
        };

        var result = Validate(proposed, current: Resolved());

        Assert.Equal(valid, result.IsValid);
        if (!valid)
        {
            Assert.Contains(result.Problems, p => p.StartsWith(key, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Undefined_enum_value_is_refused_without_throwing()
    {
        var result = Validate(Resolved() with { Mode = (ConsentMode)42 });

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, p => p.StartsWith(ConsentSettingKeys.Mode, StringComparison.Ordinal));
    }

    [Fact]
    public void Undefined_screenshot_and_post_values_are_refused()
    {
        var result = Validate(Resolved() with { Screenshot = (ScreenshotMode)9, IssueDiagnosisPost = (DiagnosisPostMode)9 });

        Assert.Equal(2, result.Problems.Count);
    }

    [Fact]
    public void Extra_patterns_must_be_a_non_null_list_of_unique_non_blank_entries()
    {
        Assert.False(Validate(Resolved() with { RedactionExtraPatterns = null! }).IsValid);
        Assert.False(Validate(Resolved() with { RedactionExtraPatterns = [" "] }).IsValid);
        Assert.False(Validate(Resolved() with { RedactionExtraPatterns = ["internal-host", "internal-host"] }).IsValid);
        Assert.True(Validate(Resolved() with { RedactionExtraPatterns = ["internal-host", "project-codename"] }).IsValid);
    }

    [Fact]
    public void Extra_patterns_never_need_approval()
    {
        var result = Validate(Resolved() with { RedactionExtraPatterns = ["internal-host"] }, current: Resolved());

        Assert.True(result.IsValid);
        Assert.Empty(result.RequiresOwnerApproval);
    }

    [Fact]
    public void Every_problem_is_reported_together()
    {
        var proposed = Resolved() with
        {
            Mode = ConsentMode.Auto,
            Screenshot = ScreenshotMode.Ask,
            LogLines = 900,
            IssueRepo = "bad repo",
        };

        var result = Validate(proposed, current: Resolved());

        Assert.False(result.IsValid);
        Assert.Equal(3, result.Problems.Count);
    }

    [Fact]
    public void Problems_are_listed_in_check_order_and_none_is_null()
    {
        var result = Validate(Resolved() with { LogLines = 900, IssueRepo = "x" });

        Assert.All(result.Problems, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.StartsWith(ConsentSettingKeys.LogLines, result.Problems[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConsentMode.Ask, ConsentMode.Auto, true)]
    [InlineData(ConsentMode.Off, ConsentMode.Auto, true)]
    [InlineData(ConsentMode.Auto, ConsentMode.Auto, false)]
    [InlineData(ConsentMode.Auto, ConsentMode.Ask, false)]
    [InlineData(ConsentMode.Auto, ConsentMode.Off, false)]
    [InlineData(ConsentMode.Ask, ConsentMode.Off, false)]
    public void Mode_approval_is_needed_only_when_moving_into_auto(ConsentMode from, ConsentMode to, bool approval)
    {
        var result = Validate(Resolved() with { Mode = to }, current: Resolved() with { Mode = from, Screenshot = ScreenshotMode.Off });

        Assert.Equal(approval, result.RequiresOwnerApproval.Contains(ConsentSettingKeys.Mode));
    }

    [Fact]
    public void Approval_is_judged_against_the_current_value_not_the_defaults()
    {
        var current = Resolved() with { Mode = ConsentMode.Auto };

        var result = Validate(Resolved() with { Mode = ConsentMode.Auto }, current: current);

        Assert.DoesNotContain(ConsentSettingKeys.Mode, result.RequiresOwnerApproval);
    }

    [Fact]
    public void Null_current_is_treated_as_the_defaults()
    {
        var result = Validate(Resolved() with { Mode = ConsentMode.Auto }, current: null);

        Assert.Contains(ConsentSettingKeys.Mode, result.RequiresOwnerApproval);
    }

    [Fact]
    public void Screenshot_allowed_needs_approval_only_when_it_changes()
    {
        var entering = Validate(Resolved() with { Screenshot = ScreenshotMode.Allowed }, current: Resolved());
        var already = Validate(Resolved() with { Screenshot = ScreenshotMode.Allowed }, current: Resolved() with { Screenshot = ScreenshotMode.Allowed });
        var leaving = Validate(Resolved() with { Screenshot = ScreenshotMode.Ask }, current: Resolved() with { Screenshot = ScreenshotMode.Allowed });

        Assert.Contains(ConsentSettingKeys.Screenshot, entering.RequiresOwnerApproval);
        Assert.DoesNotContain(ConsentSettingKeys.Screenshot, already.RequiresOwnerApproval);
        Assert.DoesNotContain(ConsentSettingKeys.Screenshot, leaving.RequiresOwnerApproval);
    }

    [Theory]
    [InlineData(90, 90, false)]
    [InlineData(91, 90, true)]
    [InlineData(120, 100, true)]
    [InlineData(120, 120, false)]
    [InlineData(95, 120, false)]
    public void Retention_above_90_needs_approval_only_when_it_rises(int proposed, int current, bool approval)
    {
        var result = Validate(
            Resolved() with { RetentionDays = proposed },
            current: Resolved() with { RetentionDays = current });

        Assert.Equal(approval, result.RequiresOwnerApproval.Contains(ConsentSettingKeys.RetentionDays));
    }

    [Theory]
    [InlineData(30, 30, false)]
    [InlineData(31, 30, true)]
    [InlineData(20, 40, false)]
    public void Screenshot_retention_above_30_needs_approval_only_when_it_rises(int proposed, int current, bool approval)
    {
        var result = Validate(
            Resolved() with { ScreenshotRetentionDays = proposed, RetentionDays = 90 },
            current: Resolved() with { ScreenshotRetentionDays = current, RetentionDays = 90 });

        Assert.Equal(approval, result.RequiresOwnerApproval.Contains(ConsentSettingKeys.ScreenshotRetentionDays));
    }

    [Fact]
    public void Issue_repo_needs_approval_when_set_or_changed_but_not_when_cleared()
    {
        var set = Validate(Resolved() with { IssueRepo = "owner/name" }, current: Resolved());
        var changed = Validate(Resolved() with { IssueRepo = "owner/other" }, current: Resolved() with { IssueRepo = "owner/name" });
        var same = Validate(Resolved() with { IssueRepo = "owner/name" }, current: Resolved() with { IssueRepo = "owner/name" });
        var cleared = Validate(Resolved() with { IssueRepo = string.Empty }, current: Resolved() with { IssueRepo = "owner/name" });

        Assert.Contains(ConsentSettingKeys.IssueRepo, set.RequiresOwnerApproval);
        Assert.Contains(ConsentSettingKeys.IssueRepo, changed.RequiresOwnerApproval);
        Assert.DoesNotContain(ConsentSettingKeys.IssueRepo, same.RequiresOwnerApproval);
        Assert.Empty(cleared.RequiresOwnerApproval);
    }

    [Fact]
    public void Diagnosis_auto_and_post_on_need_approval_when_entered()
    {
        var result = Validate(
            Resolved() with { Diagnosis = ConsentMode.Auto, IssueDiagnosisPost = DiagnosisPostMode.On },
            current: Resolved());

        Assert.Contains(ConsentSettingKeys.Diagnosis, result.RequiresOwnerApproval);
        Assert.Contains(ConsentSettingKeys.IssueDiagnosisPost, result.RequiresOwnerApproval);
    }

    [Fact]
    public void Tightening_never_needs_approval()
    {
        var current = Resolved() with
        {
            Mode = ConsentMode.Auto,
            Screenshot = ScreenshotMode.Allowed,
            RetentionDays = 200,
            ScreenshotRetentionDays = 60,
            IssueRepo = "owner/name",
            Diagnosis = ConsentMode.Auto,
            IssueDiagnosisPost = DiagnosisPostMode.On,
            NpuDiagnosis = true,
        };
        var proposed = current with
        {
            Mode = ConsentMode.Ask,
            Screenshot = ScreenshotMode.Off,
            RetentionDays = 90,
            ScreenshotRetentionDays = 30,
            IssueRepo = string.Empty,
            Diagnosis = ConsentMode.Ask,
            IssueDiagnosisPost = DiagnosisPostMode.Off,
            NpuDiagnosis = false,
        };

        var result = Validate(proposed, current: current);

        Assert.True(result.IsValid);
        Assert.Empty(result.RequiresOwnerApproval);
    }

    [Fact]
    public void Null_settings_or_context_are_rejected_with_an_exception()
    {
        Assert.Throws<ArgumentNullException>(() => ConsentSettingsValidator.Validate(null!, null, Context()));
        Assert.Throws<ArgumentNullException>(() => ConsentSettingsValidator.Validate(Resolved(), null, null!));
    }
}
