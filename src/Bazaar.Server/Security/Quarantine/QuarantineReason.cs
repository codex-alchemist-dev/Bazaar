namespace Bazaar.Server.Security.Quarantine;

/// <summary>Why content or an agent was quarantined (doc 15, "Quarantine", triggers).</summary>
public enum QuarantineReason
{
    /// <summary>The owner quarantined it.</summary>
    OwnerAction,

    /// <summary>An anomaly rule fired: rate, repeated denials.</summary>
    AnomalyRule,

    /// <summary>A canary credential was used.</summary>
    CanaryHit,

    /// <summary>A Soul was imported and is not yet approved.</summary>
    ImportedSoulUnapproved,

    /// <summary>Scam mail.</summary>
    ScamMail,
}
