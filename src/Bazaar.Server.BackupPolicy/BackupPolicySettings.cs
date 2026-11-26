using Bazaar.Server.Settings;

namespace Bazaar.Server.BackupPolicy;

/// <summary>
/// The operator's backup policy keys (doc 33 settings table, doc 45 step 8). All install scope and operator-changed. Each default is
/// the safest value for something the design left open (CLAUDE.md configurability rule): no target, the doc 33 drill interval, and
/// no acknowledgement. A target that is not on a local path sends data off the install, so it needs owner approval (doc 33:
/// "Data leaves the install"). Offsite composition (<c>backup.offsite_*</c>) stays with the Backup module.
/// </summary>
public static class BackupPolicySettings
{
    public static readonly SettingKey<string> Target = new(
        BackupPolicySettingNames.Target, string.Empty, SettingScope.Install, SettingRole.Operator,
        "Where backups go, as scheme:location (file, s3, b2 or custom). Empty means none is configured. A target off this install needs owner approval.",
        ApprovalTier.Tier2,
        validate: CheckTarget,
        requiresApprovalFor: LeavesInstall);

    public static readonly SettingKey<int> DrillIntervalDays = new(
        BackupPolicySettingNames.DrillIntervalDays, BackupPolicySettingNames.DefaultDrillIntervalDays, SettingScope.Install, SettingRole.Operator,
        "Days between restore drills.",
        validate: value => value is < 1 or > BackupPolicySettingNames.MaxDrillIntervalDays
            ? $"must be 1 to {BackupPolicySettingNames.MaxDrillIntervalDays}."
            : null);

    public static readonly SettingKey<bool> NoTargetAcknowledged = new(
        BackupPolicySettingNames.NoTargetAcknowledged, false, SettingScope.Install, SettingRole.Operator,
        "The operator finished setup without a backup target and acknowledged it. Kodo keeps flagging the missing target.");

    public static IReadOnlyList<SettingDefinition> All { get; } = [Target, DrillIntervalDays, NoTargetAcknowledged];

    /// <summary>
    /// A target is empty (none) or <c>scheme:location</c> with a known lower-case scheme, a location, no whitespace or control
    /// characters, and no credentials in the text (a secret belongs in the Identity Service, never in a setting).
    /// </summary>
    internal static string? CheckTarget(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == value.Length - 1)
        {
            return "must be empty or scheme:location, for example file:/mnt/backup/bazaar.";
        }

        if (!BackupPolicySettingNames.Schemes.Contains(value[..colon], StringComparer.Ordinal))
        {
            return $"scheme must be one of {string.Join(", ", BackupPolicySettingNames.Schemes)}.";
        }

        if (value.Length > 1024 || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return "must be at most 1024 characters with no whitespace or control characters.";
        }

        var location = value[(colon + 1)..];
        var at = location.IndexOf('@', StringComparison.Ordinal);
        if (at > 0 && location[..at].Contains(':', StringComparison.Ordinal))
        {
            return "must not carry credentials; reference a secret instead.";
        }

        return null;
    }

    private static bool LeavesInstall(string value) =>
        value.Length > 0 && !value.StartsWith(BackupPolicySettingNames.LocalScheme + ":", StringComparison.Ordinal);
}
