using Bazaar.Server.Council.Hiring;

namespace Bazaar.Server.TalentPool;

/// <summary>
/// The Talent Pool (docs/design/25, work package 8). Templates enter quarantined. A Council hire vote through
/// <see cref="IHireGate"/> releases one version; only a released version can be instantiated, and a tool grant still needs its
/// own Tier 2 approval. Fails closed.
/// </summary>
public sealed class TalentPoolService(
    ITalentTemplateStore store,
    IHireGate gate,
    IHireDecisionStore decisions,
    ITalentModelWhitelist whitelist,
    ITalentToolGrantApproval toolApproval,
    ITalentInstantiator instantiator,
    TimeProvider time) : ITalentPool, IDisposable
{
    private readonly SemaphoreSlim _submitGate = new(1, 1);

    /// <inheritdoc />
    public void Dispose() => _submitGate.Dispose();

    /// <inheritdoc />
    public async Task<TemplateSubmitResult> SubmitAsync(SoulManifest manifest, string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ManifestValidator.Validate(manifest, whitelist);
        var id = ManifestValidator.TemplateIdFor(manifest.Name);
        var sha = ManifestValidator.ContentSha(manifest);

        await _submitGate.WaitAsync(ct);
        try
        {
            var latest = await store.FindAsync(id, null, ct);
            if (latest is not null && latest.ContentSha == sha)
            {
                return new TemplateSubmitResult(id, latest.Version, sha, IsDuplicate: true);
            }

            var version = (latest?.Version ?? 0) + 1;
            await store.AddAsync(new TalentTemplate(id, version, manifest, sha, actorId, time.GetUtcNow()), ct);
            return new TemplateSubmitResult(id, version, sha, IsDuplicate: false);
        }
        finally
        {
            _submitGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TalentTemplateView?> GetAsync(string templateId, int? version, CancellationToken ct)
    {
        var t = await store.FindAsync(templateId, version, ct);
        return t is null ? null : new TalentTemplateView(t, await StandingAsync(t, ct));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TalentTemplateView>> ListAsync(CancellationToken ct)
    {
        var views = new List<TalentTemplateView>();
        foreach (var t in await store.ListLatestAsync(ct))
        {
            views.Add(new TalentTemplateView(t, await StandingAsync(t, ct)));
        }

        return views;
    }

    /// <inheritdoc />
    public async Task<TemplateStanding> ProposeHireAsync(string templateId, int version, Guid gcId, string proposerId, DateTimeOffset deadline, CancellationToken ct)
    {
        var t = await store.FindAsync(templateId, version, ct) ?? throw new KeyNotFoundException($"{templateId}@{version} is not in the pool.");
        var state = await gate.RequestAsync(new HireRequest(t.HireKey, gcId, proposerId, t.Manifest.SourceSoulId, deadline), ct);
        return Map(state.Status);
    }

    /// <inheritdoc />
    public async Task<string> InstantiateAsync(string templateId, int version, string actorId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        var t = await store.FindAsync(templateId, version, ct) ?? throw new InstantiationRefusedException($"{templateId}@{version} is not in the pool.");

        if (!await gate.IsReleasedAsync(t.HireKey, ct))
        {
            throw new InstantiationRefusedException($"{t.HireKey} has no passed Council hire vote; it stays quarantined.");
        }

        foreach (var model in t.Manifest.ModelWhitelist)
        {
            if (!whitelist.IsAllowed(model))
            {
                throw new InstantiationRefusedException($"Model '{model}' is no longer on the install whitelist.");
            }
        }

        if (t.Manifest.ToolAllowlist.Count > 0 && !await toolApproval.IsApprovedAsync(t.HireKey, t.Manifest.ToolAllowlist, ct))
        {
            throw new InstantiationRefusedException($"{t.HireKey} asks for tools and has no Tier 2 approval. A hire vote does not grant tools.");
        }

        return await instantiator.CreateAgentAsync(t, actorId, ct);
    }

    private async Task<TemplateStanding> StandingAsync(TalentTemplate t, CancellationToken ct)
    {
        var state = await decisions.FindAsync(t.HireKey, ct);
        return state is null ? TemplateStanding.Quarantined : Map(state.Status);
    }

    private static TemplateStanding Map(HireStatus s) => s switch
    {
        HireStatus.Pending => TemplateStanding.VotePending,
        HireStatus.Released => TemplateStanding.Released,
        _ => TemplateStanding.Blocked,
    };
}
