using Bazaar.Server.Components.Backup.Offsite;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Components.Backup;

/// <summary>
/// The offsite <c>backup.*</c> settings from doc 33 (settings table and "Offsite composition [PROPOSED]"). The host adds
/// <see cref="All"/> to its settings registry. Every key changes where data leaves the install, so each needs owner approval.
/// </summary>
public static class BackupSettings
{
    private static readonly SettingRole _operatorOrOwner = SettingRole.Operator | SettingRole.Owner;

    public static readonly SettingKey<OffsiteTargetKind> OffsiteTarget = new(
        "backup.offsite_target", OffsiteTargetKind.None, SettingScope.Install, _operatorOrOwner,
        "Offsite store: none, restic_s3, restic_b2 or custom. Nothing leaves the install while none.", ApprovalTier.Tier2);

    public static readonly SettingKey<string> OffsiteResticPath = new(
        "backup.offsite_restic_path", "restic", SettingScope.Install, _operatorOrOwner,
        "The restic executable.", ApprovalTier.Tier2);

    public static readonly SettingKey<string> OffsiteRepository = new(
        "backup.offsite_repository", string.Empty, SettingScope.Install, _operatorOrOwner,
        "The restic repository, for example s3:https://host/bucket.", ApprovalTier.Tier2);

    public static readonly SettingKey<string> OffsiteCredentialsRef = new(
        "backup.offsite_credentials_ref", string.Empty, SettingScope.Install, _operatorOrOwner,
        "A secret reference (never a value) to the restic password and storage keys.", ApprovalTier.Tier2);

    public static IReadOnlyList<SettingDefinition> All { get; } = [OffsiteTarget, OffsiteResticPath, OffsiteRepository, OffsiteCredentialsRef];
}
