using Bazaar.Server.Settings;

namespace Bazaar.Server.CompanyBuild.Settings;

/// <summary>
/// The company build's operator settings (doc 37 section 4a: a failed build retries a few times, then stops and reports). The doc
/// leaves the count open, so it is a setting with a safe shipped default (CLAUDE.md configurability rule). The bounds are the
/// runner's own, so a stored value can never be one the runner refuses.
/// </summary>
public static class CompanyBuildSettings
{
    /// <summary>Attempts per build target in total (the first try plus the retries). Default 3, from 1 to 10.</summary>
    public static readonly SettingKey<int> MaxAttempts = new(
        "companyBuild.retry.maxAttempts", CompanyBuildRunner.DefaultMaxAttempts, SettingScope.Install, SettingRole.Operator,
        "Attempts per company build target in total (the first try plus the retries) before the build stops and reports.",
        validate: value => value is >= 1 and <= CompanyBuildRunner.MaxAllowedAttempts
            ? null
            : $"'companyBuild.retry.maxAttempts' must be from 1 to {CompanyBuildRunner.MaxAllowedAttempts}; got {value}.");

    /// <summary>Every key this module registers.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } = [MaxAttempts];
}

/// <summary>The company build's contribution to the settings registry.</summary>
public sealed class CompanyBuildSettingContribution : ISettingContribution
{
    /// <inheritdoc />
    public IReadOnlyList<SettingDefinition> Settings => CompanyBuildSettings.All;
}
