namespace Bazaar.Server.Setup;

/// <summary>
/// The checks this build registers (doc 45 section 1.12). <see cref="Pure"/> runs from the plan alone. <see cref="Infrastructure"/> lists
/// the checks that need real services; each one is a <see cref="NotEvaluatedCheck"/> until the Host adapter supplies the real check.
/// <see cref="All"/> is the order the report lists them in.
/// </summary>
public static class SetupVerificationChecks
{
    /// <summary>The checks that read the plan only, in the order the report shows them.</summary>
    public static IReadOnlyList<ISetupVerificationCheck> Pure { get; } =
    [
        new StatusBindCheck(),
        new BackupTargetCheck(),
        new OwnerSecondFactorCheck(),
        new SecretReferenceSyntaxCheck(),
        new AuditAnchorTargetCheck(),
        new IdentityKekCustodyCheck(),
    ];

    /// <summary>The placeholders for checks that need the Host adapter (doc 45 section 1.12). None of them can report green.</summary>
    public static IReadOnlyList<ISetupVerificationCheck> Infrastructure { get; } =
    [
        new NotEvaluatedCheck("schema.migrations", "Schema version and migration state"),
        new NotEvaluatedCheck("instance.lock", "Instance lock"),
        new NotEvaluatedCheck("database.postgres", "PostgreSQL and pgvector health"),
        new NotEvaluatedCheck("providers.roles", "Provider roles usable"),
        new NotEvaluatedCheck("secrets.resolution", "Secret references resolve"),
        new NotEvaluatedCheck("audit.chainHead", "Audit chain head writable"),
        new NotEvaluatedCheck("audit.anchorReachable", "Audit anchor reachable"),
        new NotEvaluatedCheck("backups.reachable", "Backup target reachable"),
        new NotEvaluatedCheck("backups.drill", "Restore drill scheduled"),
        new NotEvaluatedCheck("host.clock", "Clock sync"),
        new NotEvaluatedCheck("host.disk", "Disk headroom"),
        new NotEvaluatedCheck("models.reachable", "Model sources reachable"),
        new NotEvaluatedCheck("pairing.offer", "Pairing offer"),
        new NotEvaluatedCheck("server.identity", "Server identity matches the company build"),
    ];

    /// <summary>Every check in report order: the pure checks, then the placeholders.</summary>
    public static IReadOnlyList<ISetupVerificationCheck> All { get; } = [.. Pure, .. Infrastructure];
}
