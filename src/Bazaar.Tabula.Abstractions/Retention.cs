namespace Bazaar.Tabula;

/// <summary>Audit class a retention rule applies to (doc 81 section 7.2). Retention is per class because only some rows may ever be pruned.</summary>
public enum AuditClass
{
    Security,
    Admin,
    DataWrite,
    DataRead,
    System,
}

/// <summary>How long a class is kept.</summary>
public enum RetentionMode
{
    /// <summary>Kept forever. The default.</summary>
    Indefinite,

    /// <summary>Kept for <see cref="AuditRetentionRule.Days"/> days.</summary>
    Fixed,
}

/// <summary>Retention for one class. <see cref="Days"/> is set exactly when <see cref="Mode"/> is <see cref="RetentionMode.Fixed"/>.</summary>
public sealed record AuditRetentionRule(AuditClass Class, RetentionMode Mode, int? Days);

/// <summary>The cutoff is a pure function of policy and time. Legal holds are the host's concern, not this policy's.</summary>
public interface IAuditRetentionPolicy
{
    AuditRetentionRule RuleFor(AuditClass auditClass);

    /// <summary>Rows of this class older than the returned instant may be pruned; null when the class is never pruned.</summary>
    DateTimeOffset? Horizon(AuditClass auditClass, DateTimeOffset now);
}
