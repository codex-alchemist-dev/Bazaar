namespace Bazaar.Tabula;

/// <summary>
/// Retention per audit class (doc 81 section 7, doc 85 R-15). The default is keep forever. Security, Admin and System are
/// never pruned and cannot be set to a window. Floors for prunable classes are supplied by the host, so this library hard-codes
/// no number; a window shorter than its floor is refused.
/// </summary>
public sealed class AuditRetentionPolicy : IAuditRetentionPolicy
{
    private readonly Dictionary<AuditClass, AuditRetentionRule> _rules = [];

    /// <summary>Keep-forever policy for every class.</summary>
    public static AuditRetentionPolicy KeepForever { get; } = new([], null);

    /// <param name="rules">Rules to apply; classes not listed stay indefinite.</param>
    /// <param name="floorDays">Minimum window per class; classes not listed have no floor beyond 1 day.</param>
    /// <exception cref="ArgumentException">A rule is malformed, targets a never-pruned class, or is below its floor.</exception>
    public AuditRetentionPolicy(IEnumerable<AuditRetentionRule> rules, IReadOnlyDictionary<AuditClass, int>? floorDays = null)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (AuditRetentionRule rule in rules)
        {
            if (!_rules.TryAdd(rule.Class, rule))
            {
                throw new ArgumentException($"Duplicate rule for {rule.Class}.", nameof(rules));
            }

            Validate(rule, floorDays);
        }
    }

    /// <summary>Whether rows of the class may ever be pruned. Security, Admin and System never are.</summary>
    public static bool IsPrunable(AuditClass auditClass) => auditClass is AuditClass.DataRead or AuditClass.DataWrite;

    public AuditRetentionRule RuleFor(AuditClass auditClass) =>
        _rules.TryGetValue(auditClass, out AuditRetentionRule? rule)
            ? rule
            : new AuditRetentionRule(auditClass, RetentionMode.Indefinite, null);

    public DateTimeOffset? Horizon(AuditClass auditClass, DateTimeOffset now)
    {
        AuditRetentionRule rule = RuleFor(auditClass);
        return rule.Mode == RetentionMode.Fixed ? now.AddDays(-rule.Days!.Value) : null;
    }

    private static void Validate(AuditRetentionRule rule, IReadOnlyDictionary<AuditClass, int>? floorDays)
    {
        if (rule.Mode == RetentionMode.Indefinite)
        {
            if (rule.Days is not null)
            {
                throw new ArgumentException($"{rule.Class}: an indefinite rule carries no days.");
            }

            return;
        }

        if (!IsPrunable(rule.Class))
        {
            throw new ArgumentException($"{rule.Class} is never pruned and cannot have a fixed window.");
        }

        int floor = 1;
        if (floorDays is not null && floorDays.TryGetValue(rule.Class, out int configured))
        {
            floor = Math.Max(floor, configured);
        }

        if (rule.Days is not int days || days < floor)
        {
            throw new ArgumentException($"{rule.Class}: window must be at least {floor} days.");
        }
    }
}
