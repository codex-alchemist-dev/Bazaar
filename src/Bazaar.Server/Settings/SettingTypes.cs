namespace Bazaar.Server.Settings;

/// <summary>Where a setting value applies (doc 03 "Settings registry"). Install values carry an empty scope id.</summary>
public enum SettingScope
{
    Install,
    Project,
    Agent,
}

/// <summary>Who may change a setting. Flags, so one actor can hold several roles.</summary>
[Flags]
public enum SettingRole
{
    None = 0,

    /// <summary>The install's operator, through the setup wizard or advanced settings (doc 03, doc 27, doc 32).</summary>
    Operator = 1,

    /// <summary>The owner, the boss and final tie-breaker (CLAUDE.md).</summary>
    Owner = 2,

    /// <summary>An agent acting inside its own scope. No key uses it yet.</summary>
    Agent = 4,
}

/// <summary>Approval a change needs before it applies (doc 15 tiers). The registry uses Tier 2 only.</summary>
public enum ApprovalTier
{
    None = 0,

    /// <summary>Owner approval (doc 15 Tier 2). The change is held, not applied, until it is granted.</summary>
    Tier2 = 2,
}

/// <summary>Result of one write attempt.</summary>
public enum SettingWriteOutcome
{
    /// <summary>The value was stored (or was already stored, which is a no-op).</summary>
    Accepted,

    /// <summary>The value is valid but needs approval; nothing was stored.</summary>
    PendingApproval,

    /// <summary>Unknown key, wrong type, invalid value, floor breach, or the actor may not change the key.</summary>
    Rejected,
}
