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
        new NotEvaluatedCheck("audit.chainHead", "Audit chain head writable", "writing the chain head is a write, and verification is read-only until a sandbox copy exists"),
        new NotEvaluatedCheck("audit.anchorReachable", "Audit anchor reachable"),
        new NotEvaluatedCheck("backups.reachable", "Backup target reachable"),
        new NotEvaluatedCheck("backups.drill", "Restore drill scheduled", "no restore-drill schedule store exists yet"),
        new NotEvaluatedCheck("host.clock", "Clock sync", "setup has no reference time source"),
        new NotEvaluatedCheck("host.disk", "Disk headroom"),
        new NotEvaluatedCheck("host.os", "Operating system supported"),
        new NotEvaluatedCheck("host.ports", "Status port free"),
        new NotEvaluatedCheck("host.dataRoot", "Data root present"),
        new NotEvaluatedCheck("models.reachable", "Model sources reachable"),
        new NotEvaluatedCheck("pairing.offer", "Pairing offer", "no pairing offer store exists yet"),
        new NotEvaluatedCheck("remote.reachable", "Remote access reachable"),
        new NotEvaluatedCheck("server.identity", "Server identity matches the company build"),
    ];

    /// <summary>
    /// The full check list with real probes where they exist: every <see cref="Infrastructure"/> check is answered by the matching
    /// <paramref name="probes"/> entry (by check id), and <c>host.disk</c> and <c>host.clock</c> run Kodo's probes when
    /// <paramref name="hostReadings"/> is given. A check in <paramref name="replacements"/> (matched by id, for a module's own check such as
    /// <c>server.identity</c>) wins over both. A check with neither is a not-evaluated placeholder, never green. Order is unchanged.
    /// </summary>
    public static IReadOnlyList<ISetupVerificationCheck> Create(
        IEnumerable<ISetupInfrastructureProbe>? probes = null,
        Bazaar.Kodo.Probes.Host.IHostReadings? hostReadings = null,
        IEnumerable<string>? diskVolumes = null,
        IEnumerable<ISetupVerificationCheck>? replacements = null)
    {
        var replacementById = new Dictionary<string, ISetupVerificationCheck>(StringComparer.Ordinal);
        foreach (var replacement in replacements ?? [])
        {
            if (!replacementById.TryAdd(replacement.Id, replacement))
            {
                throw new ArgumentException($"Two replacements answer the check '{replacement.Id}'.", nameof(replacements));
            }
        }

        var byId = new Dictionary<string, ISetupInfrastructureProbe>(StringComparer.Ordinal);
        foreach (var probe in probes ?? [])
        {
            if (!byId.TryAdd(probe.CheckId, probe))
            {
                throw new ArgumentException($"Two probes answer the check '{probe.CheckId}'.", nameof(probes));
            }
        }

        var volumes = (diskVolumes ?? []).ToList();
        var checks = new List<ISetupVerificationCheck>(Pure);
        foreach (var placeholder in Infrastructure)
        {
            checks.Add(replacementById.TryGetValue(placeholder.Id, out var replacement) ? replacement : placeholder.Id switch
            {
                "host.disk" when hostReadings is not null && volumes.Count > 0 => new KodoDiskHeadroomCheck(hostReadings, volumes),
                "host.clock" when hostReadings is not null => new KodoClockSyncCheck(hostReadings),
                _ => byId.TryGetValue(placeholder.Id, out var probe) ? new ProbeBackedCheck(placeholder.Id, placeholder.Title, probe) : placeholder,
            });
        }

        return checks;
    }

    /// <summary>Every check in report order: the pure checks, then the placeholders.</summary>
    public static IReadOnlyList<ISetupVerificationCheck> All { get; } = [.. Pure, .. Infrastructure];
}
