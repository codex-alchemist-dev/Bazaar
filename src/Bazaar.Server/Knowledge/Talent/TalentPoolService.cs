using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Knowledge.Talent;

/// <summary>
/// Service implementation of <see cref="ITalentPool"/> (doc 25 §2, WP 8).
/// </summary>
public sealed class TalentPoolService : ITalentPool
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ITalentPoolStore _store;
    private readonly TalentManifestValidator _validator;
    private readonly TimeProvider _timeProvider;

    public TalentPoolService(
        ITalentPoolStore store,
        TalentManifestValidator validator,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TemplateSubmitResult> SubmitAsync(
        TalentManifest manifest,
        string actor,
        Guid? sourceSoulId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var validation = _validator.Validate(manifest);
        if (!validation.IsValid)
        {
            return new TemplateSubmitResult(false, validation.Error);
        }

        var contentSha = ComputeContentSha(manifest);

        var existing = await _store.FindAsync(manifest.TemplateId, null, ct).ConfigureAwait(false);
        if (existing != null && existing.ContentSha.Equals(contentSha, StringComparison.OrdinalIgnoreCase))
        {
            return new TemplateSubmitResult(false, $"A template with identical content is already submitted at version {existing.Version}.");
        }

        var nextVersion = await _store.GetNextVersionAsync(manifest.TemplateId, ct).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var template = new TalentTemplate(
            TemplateId: manifest.TemplateId,
            Version: nextVersion,
            Manifest: manifest,
            ContentSha: contentSha,
            Status: TalentTemplateStatus.Quarantined, // Quarantined by default pending Council hire motion (C32)
            SourceSoulId: sourceSoulId,
            SubmittedBy: actor,
            CreatedAt: now);

        await _store.SaveAsync(template, ct).ConfigureAwait(false);

        return new TemplateSubmitResult(true, null, template);
    }

    public Task<TalentTemplate?> GetTemplateAsync(string templateId, int? version = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        return _store.FindAsync(templateId, version, ct);
    }

    public Task<IReadOnlyList<TalentTemplate>> ListTemplatesAsync(TalentTemplateStatus? status = null, CancellationToken ct = default)
    {
        return _store.ListAsync(status, ct);
    }

    public async Task<string> GetDiffAsync(string templateId, int versionA, int versionB, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var a = await _store.FindAsync(templateId, versionA, ct).ConfigureAwait(false);
        if (a == null)
        {
            throw new KeyNotFoundException($"Template '{templateId}' version {versionA} not found.");
        }

        var b = await _store.FindAsync(templateId, versionB, ct).ConfigureAwait(false);
        if (b == null)
        {
            throw new KeyNotFoundException($"Template '{templateId}' version {versionB} not found.");
        }

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Diff for '{templateId}': v{versionA} -> v{versionB}");

        if (a.Manifest.Name != b.Manifest.Name)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Name: '{a.Manifest.Name}' -> '{b.Manifest.Name}'");
        }

        if (a.Manifest.Role != b.Manifest.Role)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Role: '{a.Manifest.Role}' -> '{b.Manifest.Role}'");
        }

        var toolsAdded = b.Manifest.ToolAllowlist.Except(a.Manifest.ToolAllowlist, StringComparer.Ordinal).ToList();
        var toolsRemoved = a.Manifest.ToolAllowlist.Except(b.Manifest.ToolAllowlist, StringComparer.Ordinal).ToList();
        if (toolsAdded.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Tools Added: {string.Join(", ", toolsAdded)}");
        }
        if (toolsRemoved.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Tools Removed: {string.Join(", ", toolsRemoved)}");
        }

        var modelsAdded = b.Manifest.ModelWhitelist.Except(a.Manifest.ModelWhitelist, StringComparer.Ordinal).ToList();
        var modelsRemoved = a.Manifest.ModelWhitelist.Except(b.Manifest.ModelWhitelist, StringComparer.Ordinal).ToList();
        if (modelsAdded.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Models Added: {string.Join(", ", modelsAdded)}");
        }
        if (modelsRemoved.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Models Removed: {string.Join(", ", modelsRemoved)}");
        }

        if (a.Manifest.Prompt != b.Manifest.Prompt)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Prompt changed (length: {a.Manifest.Prompt.Length} -> {b.Manifest.Prompt.Length} chars)");
        }

        return sb.ToString();
    }

    public async Task<TemplateHireResult> ApplyHireMotionAsync(
        string templateId,
        int version,
        string approvedBy,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvedBy);

        var template = await _store.FindAsync(templateId, version, ct).ConfigureAwait(false);
        if (template == null)
        {
            return new TemplateHireResult(false, $"Template '{templateId}' version {version} not found.");
        }

        if (template.Status != TalentTemplateStatus.Quarantined)
        {
            return new TemplateHireResult(false, $"Template '{templateId}' version {version} is in status '{template.Status}', expected '{TalentTemplateStatus.Quarantined}'.");
        }

        var now = _timeProvider.GetUtcNow();
        var success = await _store.UpdateStatusAsync(
            templateId,
            version,
            TalentTemplateStatus.Active,
            approvedBy,
            now,
            ct).ConfigureAwait(false);

        if (!success)
        {
            return new TemplateHireResult(false, "Failed to update template status in store.");
        }

        var updated = template with
        {
            Status = TalentTemplateStatus.Active,
            ApprovedBy = approvedBy,
            ActivatedAt = now,
        };

        return new TemplateHireResult(true, null, updated);
    }

    private static string ComputeContentSha(TalentManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
