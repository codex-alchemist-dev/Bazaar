namespace Bazaar.Server.Setup;

/// <summary>
/// The audit anchor target is configured (doc 45 section 1.4: "Required, no default"; doc 14: mounts stay disabled until it is set).
/// The validator accepts an omitted target, so this check is stricter on purpose: an unset anchor is red here. Reachability of the
/// anchor is a separate placeholder check. The URL is never echoed.
/// </summary>
public sealed class AuditAnchorTargetCheck : ISetupVerificationCheck
{
    /// <inheritdoc />
    public string Id => "audit.anchorTarget";

    /// <inheritdoc />
    public string Title => "Audit anchor target configured";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var target = context.Plan.Custody.AuditAnchorTarget;
        SetupCheckResult result = string.IsNullOrWhiteSpace(target)
            ? SetupCheckResult.Red(
                "custody.auditAnchorTarget is not set. Mounts stay disabled until it is set (doc 14).",
                "Set custody.auditAnchorTarget to an absolute https URL.")
            : SetupCheckResult.Green("An audit anchor target is configured. Its reachability is checked by its own placeholder check.");
        return Task.FromResult(result);
    }
}
