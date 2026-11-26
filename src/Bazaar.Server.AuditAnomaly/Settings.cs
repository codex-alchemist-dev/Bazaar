namespace Bazaar.Server.AuditAnomaly;

/// <summary>
/// Operator settings (doc 48 section 5.6). Rules and thresholds start from safe drafts (<see cref="Drafts"/>). The undo window
/// has a floor that a setting cannot lower: <see cref="EffectiveUndoWindow"/> clamps to it.
/// </summary>
public sealed record AnomalySettings
{
    /// <summary>The lowest undo window. A security invariant, not a setting.</summary>
    public static readonly TimeSpan UndoWindowFloor = TimeSpan.FromMinutes(1);

    public IReadOnlyList<AnomalyRule> Rules { get; init; } = Drafts();

    /// <summary>Whether a fired rule applies its actions on its own. Alerts are always raised.</summary>
    public bool AutoContainmentEnabled { get; init; }

    public TimeSpan UndoWindow { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan EffectiveUndoWindow => UndoWindow < UndoWindowFloor ? UndoWindowFloor : UndoWindow;

    /// <summary>Safe drafts: values start conservative and are meant to be tuned from measurement (doc 48 section 5.6 OPEN).</summary>
    public static IReadOnlyList<AnomalyRule> Drafts() =>
    [
        new("read-burst-personal", AnomalyRuleKind.ReadBurst, 500, TimeSpan.FromMinutes(5), AnomalySeverity.High,
            ObjectClass: "person", Actions: ContainmentActions.PauseSessions),
        new("read-outside-scope", AnomalyRuleKind.ReadOutsideScope, 1, TimeSpan.FromMinutes(5), AnomalySeverity.High,
            Actions: ContainmentActions.PauseSessions, AutoScope: ContainmentScope.Session),
        new("canary-hit", AnomalyRuleKind.CanaryHit, 1, TimeSpan.FromMinutes(1), AnomalySeverity.Critical,
            Actions: ContainmentActions.RevokeLeases | ContainmentActions.BlockEgress | ContainmentActions.PauseSessions),
        new("approval-flood", AnomalyRuleKind.ApprovalFlood, 20, TimeSpan.FromMinutes(10), AnomalySeverity.Medium),
    ];

    /// <summary>Returns the problems with these settings (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Rules)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || !ids.Add(r.Id))
                problems.Add($"Rule id '{r.Id}' is empty or repeated.");
            if (r.Threshold < 1)
                problems.Add($"Rule '{r.Id}': threshold must be at least 1.");
            if (r.Window <= TimeSpan.Zero)
                problems.Add($"Rule '{r.Id}': window must be positive.");
            if (r.Kind == AnomalyRuleKind.ReadBurst && string.IsNullOrWhiteSpace(r.ObjectClass))
                problems.Add($"Rule '{r.Id}': a read burst needs an object class.");
            if (r.AutoScope == ContainmentScope.Global)
                problems.Add($"Rule '{r.Id}': automatic containment is never global.");
            if (r.Actions.HasFlag(ContainmentActions.FreezeWrites))
                problems.Add($"Rule '{r.Id}': freezing writes is global, so it is not an automatic action.");
        }
        return problems;
    }
}
