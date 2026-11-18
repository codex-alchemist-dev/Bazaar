using System.Text.RegularExpressions;

namespace Bazaar.Diagnostics.Consent;

/// <summary>Facts about the install that the validator needs but does not own.</summary>
/// <param name="CoworkerModuleEnabled">Whether the local coworker module (doc 31) is enabled on this head.</param>
/// <param name="WhitelistedDiagnosisModels">Model ids whitelisted for the diagnosis role (doc 05). Compared ordinally.</param>
public sealed record ConsentValidationContext(bool CoworkerModuleEnabled, IReadOnlyCollection<string> WhitelistedDiagnosisModels);

/// <summary>
/// The outcome of validating a proposed <see cref="ConsentSettings"/>. Problems refuse the save and are written for
/// the person who made the change. Keys in <see cref="RequiresOwnerApproval"/> are accepted but held until the owner
/// approves them (doc 03, Tier 2).
/// </summary>
/// <param name="Problems">Every refusal found, in key order. Empty when the settings are valid.</param>
/// <param name="RequiresOwnerApproval">Keys whose proposed value moves into an owner-gated state.</param>
public sealed record ConsentValidationResult(IReadOnlyList<string> Problems, IReadOnlyList<string> RequiresOwnerApproval)
{
    /// <summary>True when nothing refuses the save. Approval holds do not make the result invalid.</summary>
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Validates crash-report settings at save (doc 43 section 7.3). Pure: it reports every problem it finds and never
/// throws for bad values. The rule for "may tighten, never loosen" is structural: approval is required only when a
/// value moves into an owner-gated state, so moving back toward the safer value never needs approval.
/// </summary>
public static class ConsentSettingsValidator
{
    private const int _maxLogLines = 500;
    private const int _maxRetentionDays = 365;
    private const int _maxScreenshotRetentionDays = 90;
    private const int _maxLocalQueueDays = 30;
    private const int _maxIssuesPerDay = 200;
    private const int _retentionApprovalAbove = 90;
    private const int _screenshotRetentionApprovalAbove = 30;
    private const int _gitHubNameMaxLength = 100;

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex _repoName = new("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, _matchTimeout);

    /// <summary>
    /// Validates <paramref name="proposed"/>. <paramref name="current"/> is the value being replaced; null means the
    /// shipped defaults (first setup). Approval is judged against <paramref name="current"/>, not against the defaults.
    /// </summary>
    public static ConsentValidationResult Validate(ConsentSettings proposed, ConsentSettings? current, ConsentValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(context);

        var baseline = current ?? ConsentSettings.Default;
        var problems = new List<string>();

        CheckEnum(problems, ConsentSettingKeys.Mode, proposed.Mode);
        CheckEnum(problems, ConsentSettingKeys.Screenshot, proposed.Screenshot);
        CheckEnum(problems, ConsentSettingKeys.Diagnosis, proposed.Diagnosis);
        CheckEnum(problems, ConsentSettingKeys.IssueDiagnosisPost, proposed.IssueDiagnosisPost);

        CheckRange(problems, ConsentSettingKeys.LogLines, proposed.LogLines, 0, _maxLogLines);
        CheckRange(problems, ConsentSettingKeys.RetentionDays, proposed.RetentionDays, 1, _maxRetentionDays);
        CheckRange(problems, ConsentSettingKeys.ScreenshotRetentionDays, proposed.ScreenshotRetentionDays, 1, _maxScreenshotRetentionDays);
        CheckRange(problems, ConsentSettingKeys.LocalQueueDays, proposed.LocalQueueDays, 1, _maxLocalQueueDays);
        CheckRange(problems, ConsentSettingKeys.IssuesPerDay, proposed.IssuesPerDay, 0, _maxIssuesPerDay);

        CheckPatterns(problems, proposed.RedactionExtraPatterns);
        CheckIssueRepo(problems, proposed.IssueRepo);
        CheckDiagnosisModel(problems, proposed.Diagnosis, proposed.DiagnosisModel, context.WhitelistedDiagnosisModels);

        if (proposed.Mode == ConsentMode.Auto && proposed.Screenshot == ScreenshotMode.Ask)
        {
            problems.Add($"{ConsentSettingKeys.Screenshot} cannot be ask when {ConsentSettingKeys.Mode} is auto: no person is present to decide.");
        }

        if (proposed.Mode == ConsentMode.Off && proposed.IssueRepo.Length > 0)
        {
            problems.Add($"{ConsentSettingKeys.IssueRepo} must be empty when {ConsentSettingKeys.Mode} is off.");
        }

        if (proposed.ScreenshotRetentionDays > proposed.RetentionDays)
        {
            problems.Add($"{ConsentSettingKeys.ScreenshotRetentionDays} ({proposed.ScreenshotRetentionDays}) cannot exceed {ConsentSettingKeys.RetentionDays} ({proposed.RetentionDays}).");
        }

        if (proposed.NpuDiagnosis && !context.CoworkerModuleEnabled)
        {
            problems.Add($"{ConsentSettingKeys.NpuDiagnosis} needs the local coworker module, which is disabled on this head.");
        }

        var approvals = new List<string>();
        AddApprovals(approvals, proposed, baseline);

        return new ConsentValidationResult(problems, approvals);
    }

    private static void CheckEnum<T>(List<string> problems, string key, T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            problems.Add($"{key} has an unknown value '{value}'.");
        }
    }

    private static void CheckRange(List<string> problems, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            problems.Add($"{key} must be from {min} to {max}; got {value}.");
        }
    }

    private static void CheckPatterns(List<string> problems, IReadOnlyList<string>? patterns)
    {
        if (patterns is null)
        {
            problems.Add($"{ConsentSettingKeys.RedactionExtraPatterns} must be a list, not null.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                problems.Add($"{ConsentSettingKeys.RedactionExtraPatterns} contains a blank entry.");
            }
            else if (!seen.Add(pattern))
            {
                problems.Add($"{ConsentSettingKeys.RedactionExtraPatterns} contains '{pattern}' more than once.");
            }
        }
    }

    private static void CheckIssueRepo(List<string> problems, string repo)
    {
        if (repo.Length == 0)
        {
            return;
        }

        if (!_repoName.IsMatch(repo))
        {
            problems.Add($"{ConsentSettingKeys.IssueRepo} must be empty or owner/name, with each part up to {_gitHubNameMaxLength} characters.");
        }
    }

    private static void CheckDiagnosisModel(List<string> problems, ConsentMode diagnosis, string? model, IReadOnlyCollection<string> whitelist)
    {
        if (model is null)
        {
            if (diagnosis != ConsentMode.Off)
            {
                problems.Add($"{ConsentSettingKeys.DiagnosisModel} must be resolved at setup while {ConsentSettingKeys.Diagnosis} is {diagnosis.ToString().ToLowerInvariant()}.");
            }

            return;
        }

        if (!whitelist.Contains(model, StringComparer.Ordinal))
        {
            problems.Add($"{ConsentSettingKeys.DiagnosisModel} '{model}' is not on the diagnosis-role whitelist.");
        }
    }

    private static void AddApprovals(List<string> approvals, ConsentSettings proposed, ConsentSettings baseline)
    {
        if (proposed.Mode == ConsentMode.Auto && baseline.Mode != ConsentMode.Auto)
        {
            approvals.Add(ConsentSettingKeys.Mode);
        }

        if (proposed.Screenshot == ScreenshotMode.Allowed && baseline.Screenshot != ScreenshotMode.Allowed)
        {
            approvals.Add(ConsentSettingKeys.Screenshot);
        }

        if (proposed.RetentionDays > _retentionApprovalAbove && proposed.RetentionDays > baseline.RetentionDays)
        {
            approvals.Add(ConsentSettingKeys.RetentionDays);
        }

        if (proposed.ScreenshotRetentionDays > _screenshotRetentionApprovalAbove && proposed.ScreenshotRetentionDays > baseline.ScreenshotRetentionDays)
        {
            approvals.Add(ConsentSettingKeys.ScreenshotRetentionDays);
        }

        if (proposed.IssueRepo.Length > 0 && proposed.IssueRepo != baseline.IssueRepo)
        {
            approvals.Add(ConsentSettingKeys.IssueRepo);
        }

        if (proposed.Diagnosis == ConsentMode.Auto && baseline.Diagnosis != ConsentMode.Auto)
        {
            approvals.Add(ConsentSettingKeys.Diagnosis);
        }

        if (proposed.IssueDiagnosisPost == DiagnosisPostMode.On && baseline.IssueDiagnosisPost != DiagnosisPostMode.On)
        {
            approvals.Add(ConsentSettingKeys.IssueDiagnosisPost);
        }

        if (proposed.NpuDiagnosis && !baseline.NpuDiagnosis)
        {
            approvals.Add(ConsentSettingKeys.NpuDiagnosis);
        }
    }
}
