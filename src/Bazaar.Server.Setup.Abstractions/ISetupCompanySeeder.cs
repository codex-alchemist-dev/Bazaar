namespace Bazaar.Server.Setup;

/// <summary>
/// The store seam of the company seed step (doc 45 steps 1 and 6, section 1.3a): it creates the company profile, the owner identity
/// and the first agents. The Postgres implementation lives in <c>Bazaar.Server.Company</c>; the step in <c>Bazaar.Server.Setup</c>
/// sees only this port, so it is tested with a fake. Another apply step (for example a first-run seed) may reuse the port to create
/// agents instead of writing its own store.
/// </summary>
/// <remarks>
/// Idempotent: a state this port already produced is left alone and reported as not created. A conflicting state (a company with
/// another slug, an owner with another name) is refused, never overwritten. Nothing in a result or a failure message is a secret.
/// </remarks>
public interface ISetupCompanySeeder
{
    /// <summary>Reads what <see cref="SeedAsync"/> would create or find. Writes nothing.</summary>
    Task<CompanySeedPreview> PreviewAsync(CompanySeedRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Creates what is missing, all or nothing: if this call fails part way it removes what it created before it throws or returns a
    /// refusal. On success the outcome says exactly what this call created, which is what <see cref="RollbackAsync"/> removes.
    /// </summary>
    Task<CompanySeedResult> SeedAsync(CompanySeedRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Removes what the outcome says one earlier <see cref="SeedAsync"/> created, when a later step fails. A row that changed after
    /// the seed (an agent that left the created state, an owner with devices) is not removed. Never touches what the seed only found.
    /// </summary>
    Task RollbackAsync(CompanySeedOutcome outcome, CancellationToken cancellationToken);
}

/// <summary>What to seed. Values come from the validated plan; <see cref="OwnerDisplayName"/> is personal data and never goes to an audit row.</summary>
public sealed record CompanySeedRequest(
    string Name,
    string Slug,
    string Locale,
    string Timezone,
    string OwnerDisplayName,
    bool RequireSecondFactor,
    IReadOnlyList<string> AgentNames);

/// <summary>The dry run of a seed: what exists and what a seed would create.</summary>
public sealed record CompanySeedPreview(
    bool CompanyExists,
    bool OwnerExists,
    IReadOnlyList<string> AgentsToCreate,
    IReadOnlyList<string> AgentsExisting,
    string? Conflict);

/// <summary>An agent a seed created.</summary>
public sealed record SeededAgent(Guid Id, string Name);

/// <summary>What one seed created. An empty outcome (all false, no ids) means everything was already there.</summary>
public sealed record CompanySeedOutcome(string Slug, bool CompanyCreated, Guid? OwnerCreatedId, IReadOnlyList<SeededAgent> AgentsCreated)
{
    /// <summary>True when the seed created nothing.</summary>
    public bool IsEmpty => !CompanyCreated && OwnerCreatedId is null && AgentsCreated.Count == 0;
}

/// <summary>The result of <see cref="ISetupCompanySeeder.SeedAsync"/>: an outcome, or a refusal with a message that names no value.</summary>
public sealed record CompanySeedResult(CompanySeedOutcome? Outcome, string? Refusal, IReadOnlyList<SeededAgent> AgentsFound)
{
    /// <summary>True when the seed ran to the end.</summary>
    public bool Succeeded => Outcome is not null;

    /// <summary>A refusal: nothing was created.</summary>
    public static CompanySeedResult Refused(string reason) => new(null, reason, []);
}
