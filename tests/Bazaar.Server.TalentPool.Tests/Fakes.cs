namespace Bazaar.Server.TalentPool.Tests;

/// <summary>In-memory store with the same rules as the Postgres one. The shared contract tests run against both.</summary>
internal sealed class InMemoryTalentTemplateStore : ITalentTemplateStore
{
    private readonly Lock _gate = new();
    private readonly List<TalentTemplate> _rows = [];

    public Task<TalentTemplateAddResult> AddVersionAsync(
        Guid templateId, ValidatedTalentManifest manifest, Guid? sourceSoulId, string submittedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);
        lock (_gate)
        {
            var existing = _rows.FirstOrDefault(r => r.TemplateId == templateId && r.ContentSha == manifest.ContentSha);
            if (existing is not null)
                return Task.FromResult(new TalentTemplateAddResult(existing, false));

            var version = _rows.Where(r => r.TemplateId == templateId).Select(r => r.Version).DefaultIfEmpty(0).Max() + 1;
            var row = new TalentTemplate(templateId, version, manifest.Manifest with { TemplateId = templateId }, manifest.ContentSha,
                TalentTemplateStatus.Quarantined, sourceSoulId);
            _rows.Add(row);
            return Task.FromResult(new TalentTemplateAddResult(row, true));
        }
    }

    public Task<TalentTemplate?> GetAsync(Guid templateId, int version, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_rows.FirstOrDefault(r => r.TemplateId == templateId && r.Version == version));
    }

    public Task<IReadOnlyList<TalentTemplate>> ListVersionsAsync(Guid templateId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<TalentTemplate>>([.. _rows.Where(r => r.TemplateId == templateId).OrderBy(r => r.Version)]);
    }

    public Task<bool> TryResolveAsync(Guid templateId, int version, TalentTemplateStatus newStatus, CancellationToken ct)
    {
        if (newStatus == TalentTemplateStatus.Quarantined)
            throw new ArgumentException("A version cannot be moved back into quarantine.", nameof(newStatus));
        lock (_gate)
        {
            var index = _rows.FindIndex(r => r.TemplateId == templateId && r.Version == version && r.Status == TalentTemplateStatus.Quarantined);
            if (index < 0)
                return Task.FromResult(false);
            _rows[index] = _rows[index] with { Status = newStatus };
            return Task.FromResult(true);
        }
    }
}

internal sealed class FakeWhitelist(params string[] models) : ITalentModelWhitelist
{
    private readonly HashSet<string> _models = new(models, StringComparer.Ordinal);

    public void Remove(string model) => _models.Remove(model);

    public Task<IReadOnlySet<string>> AllowedModelsAsync(TalentActor actor, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<string>>(_models);
}

internal sealed class FakeHireGate : ITalentHireGate
{
    public bool Passed { get; set; }

    public Task<bool> HasHirePassedAsync(Guid templateId, int version, string contentSha, CancellationToken ct) => Task.FromResult(Passed);
}

internal static class Samples
{
    public static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal) { "haiku-5-5", "sonnet-5-5" };

    public static TalentManifest Manifest(
        string prompt = "You review pull requests carefully.",
        string[]? tools = null,
        string[]? models = null,
        Guid? templateId = null) =>
        new(prompt, tools ?? ["read_file", "run_tests"], models ?? ["haiku-5-5"], templateId);

    public static ValidatedTalentManifest Validated(TalentManifest? manifest = null) =>
        TalentManifestValidator.Validate(manifest ?? Manifest(), Models).Validated!;

    public static TalentActor Actor(Guid? soul = null) => new("agent:alice", soul ?? Guid.Parse("11111111-1111-1111-1111-111111111111"));
}
