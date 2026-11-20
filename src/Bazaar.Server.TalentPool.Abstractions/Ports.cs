namespace Bazaar.Server.TalentPool;

/// <summary>Port for the install's model whitelist (owned by the gateway and per-agent model policy). The Talent Pool only reads it.</summary>
public interface ITalentModelWhitelist
{
    /// <summary>Models the actor may use. Compared ordinally.</summary>
    Task<IReadOnlySet<string>> AllowedModelsAsync(TalentActor actor, CancellationToken ct);
}

/// <summary>Durable storage for <c>talent_templates</c>. Versions are append-only; only the status of a quarantined version can change.</summary>
public interface ITalentTemplateStore
{
    /// <summary>
    /// Adds a quarantined version. The version number is the next one for <paramref name="templateId"/> (1 for a new template). If the
    /// same content hash is already stored for the template, nothing is written and the existing version comes back with Created false.
    /// </summary>
    Task<TalentTemplateAddResult> AddVersionAsync(Guid templateId, ValidatedTalentManifest manifest, Guid? sourceSoulId, string submittedBy, CancellationToken ct);

    Task<TalentTemplate?> GetAsync(Guid templateId, int version, CancellationToken ct);

    /// <summary>All versions of a template, oldest first. Empty when the template does not exist.</summary>
    Task<IReadOnlyList<TalentTemplate>> ListVersionsAsync(Guid templateId, CancellationToken ct);

    /// <summary>
    /// Moves a quarantined version to hired or rejected. Returns false when the version is missing or no longer quarantined: hired and
    /// rejected are final.
    /// </summary>
    Task<bool> TryResolveAsync(Guid templateId, int version, TalentTemplateStatus newStatus, CancellationToken ct);
}

/// <summary>Version-diff query over any <see cref="ITalentTemplateStore"/>.</summary>
public static class TalentTemplateStoreExtensions
{
    /// <summary>Diffs two stored versions. Returns null when either does not exist.</summary>
    public static async Task<TemplateVersionDiff?> DiffAsync(
        this ITalentTemplateStore store, Guid templateId, int fromVersion, int toVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var from = await store.GetAsync(templateId, fromVersion, ct).ConfigureAwait(false);
        var to = await store.GetAsync(templateId, toVersion, ct).ConfigureAwait(false);
        if (from is null || to is null)
            return null;

        var (toolsAdded, toolsRemoved) = Delta(from.Manifest.ToolAllowlist, to.Manifest.ToolAllowlist);
        var (modelsAdded, modelsRemoved) = Delta(from.Manifest.ModelWhitelist, to.Manifest.ModelWhitelist);
        return new TemplateVersionDiff(
            templateId, fromVersion, toVersion,
            !string.Equals(from.Manifest.Prompt, to.Manifest.Prompt, StringComparison.Ordinal),
            toolsAdded, toolsRemoved, modelsAdded, modelsRemoved);
    }

    private static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) Delta(IReadOnlyList<string> before, IReadOnlyList<string> after) =>
        ([.. after.Except(before, StringComparer.Ordinal).Order(StringComparer.Ordinal)],
         [.. before.Except(after, StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
}
