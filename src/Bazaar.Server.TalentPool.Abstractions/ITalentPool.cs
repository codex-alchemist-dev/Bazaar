namespace Bazaar.Server.TalentPool;

/// <summary>The Talent Pool: Soul templates waiting to be hired (docs/design/25, work package 8).</summary>
public interface ITalentPool
{
    /// <summary>
    /// Validates and stores a template version, quarantined. Rejects memory files, blank fields and models off the whitelist.
    /// An identical manifest returns the existing version.
    /// </summary>
    /// <exception cref="ManifestRejectedException">The manifest broke a rule.</exception>
    Task<TemplateSubmitResult> SubmitAsync(SoulManifest manifest, string actorId, CancellationToken ct);

    /// <summary>Gets one version, or the latest when <paramref name="version"/> is null. Null when unknown.</summary>
    Task<TalentTemplateView?> GetAsync(string templateId, int? version, CancellationToken ct);

    /// <summary>Lists the latest version of every template.</summary>
    Task<IReadOnlyList<TalentTemplateView>> ListAsync(CancellationToken ct);

    /// <summary>Opens a Council hire vote for a version. Returns the standing after the call. Idempotent while a vote is open.</summary>
    /// <exception cref="KeyNotFoundException">The template version does not exist.</exception>
    Task<TemplateStanding> ProposeHireAsync(string templateId, int version, Guid gcId, string proposerId, DateTimeOffset deadline, CancellationToken ct);

    /// <summary>
    /// Creates a new agent from a released template through the Soul system and returns the new agent id.
    /// </summary>
    /// <exception cref="InstantiationRefusedException">Not released, a model is off the whitelist, or a tool grant lacks Tier 2 approval.</exception>
    Task<string> InstantiateAsync(string templateId, int version, string actorId, CancellationToken ct);
}

/// <summary>Store port for template versions. The host provides a durable one; <c>InMemoryTalentTemplateStore</c> serves tests and dev.</summary>
public interface ITalentTemplateStore
{
    /// <summary>Appends a version. Fails if that id and version exist.</summary>
    Task AddAsync(TalentTemplate version, CancellationToken ct);

    /// <summary>Finds a version, or the latest when <paramref name="version"/> is null.</summary>
    Task<TalentTemplate?> FindAsync(string templateId, int? version, CancellationToken ct);

    /// <summary>Lists the latest version of each template.</summary>
    Task<IReadOnlyList<TalentTemplate>> ListLatestAsync(CancellationToken ct);
}

/// <summary>The install model whitelist (docs/design/25: a template whose model is outside it is refused).</summary>
public interface ITalentModelWhitelist
{
    /// <summary>True when the model (provider-qualified, <c>provider:model</c>) may be used.</summary>
    ValueTask<bool> IsAllowedAsync(string model, CancellationToken cancellationToken);
}

/// <summary>The Tier 2 approval for a template's tool grants (docs/design/15). A hire vote never implies it.</summary>
public interface ITalentToolGrantApproval
{
    /// <summary>True when the owner approved these tool grants for this template version.</summary>
    Task<bool> IsApprovedAsync(string hireKey, IReadOnlyList<string> tools, CancellationToken ct);
}

/// <summary>Creates the agent through the Soul system (docs/design/07, Creation). Provided by the host.</summary>
public interface ITalentInstantiator
{
    /// <summary>Creates an agent from the manifest and returns its id.</summary>
    Task<string> CreateAgentAsync(TalentTemplate candidate, string actorId, CancellationToken ct);
}
