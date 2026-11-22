namespace Bazaar.Kodo.Responder;

/// <summary>
/// One safe fix: a runbook with one action step, a verify step and a dry-run preview (doc 46 section 16.2). The control is a name only;
/// the role model decides who holds it (section 14.5).
/// </summary>
/// <param name="Id">The fix id, equal to its runbook id.</param>
/// <param name="Category">The category that owns the trigger (section 16.5).</param>
/// <param name="Trigger">The probe or rule id whose failure suggests this fix.</param>
/// <param name="Title">A short title.</param>
/// <param name="Definition">The runbook, to be signed and added to a <see cref="RunbookCatalogue"/>.</param>
public sealed record OneClickFix(string Id, string Category, string Trigger, string Title, RunbookDefinition Definition)
{
    /// <summary>
    /// The unattended-recovery tag: set only on the host restart and the slot switch, null on every other fix. It is the only thing the
    /// <c>kodo.recover.unattended</c> setting can enable.
    /// </summary>
    public UnattendedRecovery? Recovery { get; init; }

    /// <summary>The control a starter must hold.</summary>
    public string RequiredControl => Definition.RequiredControl;

    /// <summary>The least approval the fix accepts.</summary>
    public RunbookApprovalTier ApprovalTier => Definition.ApprovalTier;
}

/// <summary>
/// The catalogue of one-click fixes from doc 46 section 16.5. These are definitions only: they carry no signature, so none can run until
/// the release pipeline signs it and a <see cref="RunbookCatalogue"/> accepts it. Each action step changes one thing, each has a verify
/// step, and none is unattended by default (an operator names the runbook in <see cref="RunbookOptions.AutoRunbookIds"/> to allow it).
/// </summary>
/// <remarks>
/// Section 16.5 lists host restart and slot switch as "Auto", while section 14.5 makes restart, isolate and roll back step-up by default.
/// The shipped definitions follow 14.5 (step-up) and the conflict is recorded in section 16.8.
/// </remarks>
public static class OneClickFixes
{
    /// <summary>Every fix, in the order of the section 16.5 table.</summary>
    public static IReadOnlyList<OneClickFix> All { get; } =
    [
        Fix("fix.host_restart", "host", "host.ready", "Restart the host through the sentinel",
            RunbookRisk.Medium, RunbookApprovalTier.ApprovalStepUp, KodoControls.ControlRestart,
            "sentinel.status", "sentinel.restart_host", "probe.host_ready", TimeSpan.FromMinutes(2), rollback: null, UnattendedRecovery.HostRestart),
        Fix("fix.disk_space", "host", "host.disk.free", "Rotate logs and prune spill past its window",
            RunbookRisk.Medium, RunbookApprovalTier.Approval, KodoControls.RunbookRun,
            "disk.status", "disk.rotate_and_prune", "probe.disk_free", TimeSpan.FromMinutes(5), rollback: null),
        Fix("fix.replication_reseed", "db", "replication.status", "Re-seed the standby from the latest base backup",
            RunbookRisk.High, RunbookApprovalTier.ApprovalStepUp, KodoControls.FailoverApprove,
            "replication.status", "replication.reseed_standby", "probe.replication", TimeSpan.FromMinutes(30), rollback: null),
        Fix("fix.slot_switch", "kodo", "kodo.self.signature_verify", "Switch to the other verified slot",
            RunbookRisk.Medium, RunbookApprovalTier.ApprovalStepUp, KodoControls.ControlRollback,
            "slot.verify_other", "slot.switch_other", "probe.signature_verify", TimeSpan.FromMinutes(2), rollback: "slot.switch_back", UnattendedRecovery.SlotSwitch),
        Fix("fix.module_disable", "modules", "module.health_contract", "Disable the failing module and keep its data",
            RunbookRisk.Medium, RunbookApprovalTier.Approval, KodoControls.ControlIsolate,
            "module.status", "module.disable", "probe.module_state", TimeSpan.FromMinutes(2), rollback: "module.enable"),
        Fix("fix.cert_renew", "certs", "certs.server", "Renew the server certificate",
            RunbookRisk.Medium, RunbookApprovalTier.Approval, KodoControls.RunbookRun,
            "cert.status", "cert.renew", "probe.certs_server", TimeSpan.FromMinutes(5), rollback: null),
        Fix("fix.backup_now", "db", "db.backup_freshness", "Run a backup now",
            RunbookRisk.Low, RunbookApprovalTier.None, KodoControls.RunbookRun,
            "backup.status", "backup.run_now", "probe.backup_freshness", TimeSpan.FromMinutes(30), rollback: null),
        Fix("fix.model_lane_fallback", "models", "model.lane_quota", "Route to the configured fallback lane",
            RunbookRisk.Low, RunbookApprovalTier.None, KodoControls.RunbookRun,
            "lane.status", "lane.use_fallback", "probe.lane_state", TimeSpan.FromMinutes(2), rollback: "lane.use_primary"),
    ];

    /// <summary>The fixes suggested for a failing probe or rule id. A suggestion is never a start: a role with the control runs it.</summary>
    public static IReadOnlyList<OneClickFix> For(string trigger)
    {
        ArgumentException.ThrowIfNullOrEmpty(trigger);
        return All.Where(fix => string.Equals(fix.Trigger, trigger, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// The unattended-recovery tag of a catalogue entry, or null. It is non-null only when the entry is byte-for-byte (by canonical digest)
    /// the shipped host restart or slot switch, so a custom runbook that reuses a fix id, or any other fix, never matches.
    /// </summary>
    public static UnattendedRecovery? RecoveryOf(CatalogueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        foreach (OneClickFix fix in All)
        {
            if (fix.Recovery is { } recovery
                && string.Equals(fix.Id, entry.Definition.Id, StringComparison.Ordinal)
                && string.Equals(RunbookCanonical.Digest(fix.Definition), entry.Digest, StringComparison.Ordinal))
            {
                return recovery;
            }
        }

        return null;
    }

    private static OneClickFix Fix(
        string id,
        string category,
        string trigger,
        string title,
        RunbookRisk risk,
        RunbookApprovalTier tier,
        string control,
        string checkOperation,
        string actionOperation,
        string verifyOperation,
        TimeSpan actionTimeout,
        string? rollback,
        UnattendedRecovery? recovery = null)
    {
        Dictionary<string, string> none = new(StringComparer.Ordinal);
        RunbookStep[] rollbackSteps = rollback is null
            ? []
            : [new RunbookStep("undo", RunbookStepKind.Action, rollback, none, actionTimeout)];

        RunbookDefinition definition = new(
            id,
            1,
            title,
            risk,
            tier,
            control,
            actionTimeout,
            [new RunbookStep("precheck", RunbookStepKind.Check, checkOperation, none, TimeSpan.FromSeconds(30), MaxAttempts: 2)],
            [
                new RunbookStep("fix", RunbookStepKind.Action, actionOperation, none, actionTimeout),
                new RunbookStep("verify", RunbookStepKind.Verify, verifyOperation, none, TimeSpan.FromMinutes(2), MaxAttempts: 3),
            ],
            rollbackSteps);
        return new OneClickFix(id, category, trigger, title, definition) { Recovery = recovery };
    }
}
