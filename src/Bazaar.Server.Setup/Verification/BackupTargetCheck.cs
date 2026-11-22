namespace Bazaar.Server.Setup;

/// <summary>
/// A backup target is configured, or the no-target acknowledgement is recorded (doc 45 sections 1.4 and 1.12). With only the
/// acknowledgement the check is amber, because the setup can complete only with that acknowledgement named. With neither, it is red.
/// Reachability and the restore drill are separate placeholder checks. The target text is never echoed.
/// </summary>
public sealed class BackupTargetCheck : ISetupVerificationCheck
{
    /// <inheritdoc />
    public string Id => "backups.target";

    /// <inheritdoc />
    public string Title => "Backup target configured or acknowledged";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var backups = context.Plan.Backups;

        SetupCheckResult result;
        if (backups.Target is null)
        {
            result = backups.AcknowledgeNoTarget
                ? SetupCheckResult.Amber(
                    "No backup target is configured. The no-target acknowledgement is recorded, so setup can complete only with it named.",
                    "Configure backups.target so that this check is green.")
                : SetupCheckResult.Red(
                    "No backup target is configured, and no acknowledgement is recorded.",
                    "Set backups.target, or set backups.acknowledgeNoTarget to true to record that none is configured.");
        }
        else if (string.IsNullOrWhiteSpace(backups.Target))
        {
            result = SetupCheckResult.Red("backups.target is blank.", "Set backups.target to a backup location, or omit it and acknowledge that none is configured.");
        }
        else
        {
            result = SetupCheckResult.Green("A backup target is configured. Its reachability is checked by its own placeholder check.");
        }

        return Task.FromResult(result);
    }
}
