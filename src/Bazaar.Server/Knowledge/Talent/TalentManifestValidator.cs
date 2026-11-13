using System.Text.RegularExpressions;
using Bazaar.Server.Sanitization;

namespace Bazaar.Server.Knowledge.Talent;

/// <summary>
/// Result of validating a <see cref="TalentManifest"/>.
/// </summary>
public sealed record ValidationResult(bool IsValid, string? Error = null)
{
    public static ValidationResult Success { get; } = new(true);
    public static ValidationResult Failure(string error) => new(false, error);
}

/// <summary>
/// Validates talent manifests according to doc 25 rules:
/// - Rejects memory files or private paths.
/// - Rejects secret-bearing or raw credentials.
/// - Rejects non-whitelisted or wildcard models.
/// - Enforces schema and naming invariants.
/// </summary>
public sealed class TalentManifestValidator
{
    private static readonly Regex _slugRegex = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex _toolNameRegex = new("^[a-zA-Z0-9_.-]{1,64}$", RegexOptions.Compiled);

    private static readonly string[] _forbiddenMemoryPatterns =
    [
        "agents/",
        "memory/",
        "hive/",
        "failures/",
        "lessons/",
        "preferences/",
        ".md",
    ];

    private readonly ITextSanitizer? _sanitizer;

    public TalentManifestValidator(ITextSanitizer? sanitizer = null)
    {
        _sanitizer = sanitizer;
    }

    public ValidationResult Validate(TalentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrWhiteSpace(manifest.TemplateId) || !_slugRegex.IsMatch(manifest.TemplateId))
        {
            return ValidationResult.Failure($"Invalid TemplateId '{manifest.TemplateId}': must be a lowercase alphanumeric slug with hyphens up to 64 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 128)
        {
            return ValidationResult.Failure("Manifest Name must be non-empty and at most 128 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Role) || manifest.Role.Length > 256)
        {
            return ValidationResult.Failure("Manifest Role must be non-empty and at most 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Prompt) || manifest.Prompt.Length > 65536)
        {
            return ValidationResult.Failure("Manifest Prompt must be non-empty and at most 65536 characters.");
        }

        // Check for memory file or private path references
        foreach (var pattern in _forbiddenMemoryPatterns)
        {
            if (manifest.Prompt.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Failure($"Manifest Prompt contains forbidden memory reference '{pattern}'. Talent templates cannot contain memory files or private paths (doc 25).");
            }
        }

        // Check for secret patterns if sanitizer is configured
        if (_sanitizer != null)
        {
            var sanitized = _sanitizer.Sanitize(manifest.Prompt, SensitiveTerms.Empty);
            if (sanitized.Redactions.Length > 0)
            {
                return ValidationResult.Failure($"Manifest Prompt contains secret patterns or sensitive credentials that were redacted: {sanitized.Redactions[0].Kind}.");
            }
        }

        // Validate model whitelist
        if (manifest.ModelWhitelist == null || manifest.ModelWhitelist.Count == 0)
        {
            return ValidationResult.Failure("ModelWhitelist must contain at least one provider-qualified model.");
        }

        foreach (var model in manifest.ModelWhitelist)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return ValidationResult.Failure("ModelWhitelist entry cannot be empty.");
            }

            if (model.Contains('*'))
            {
                return ValidationResult.Failure($"ModelWhitelist entry '{model}' contains a wildcard, which is forbidden (doc 25).");
            }

            if (model.Equals("auto", StringComparison.OrdinalIgnoreCase) || model.StartsWith("auto/", StringComparison.OrdinalIgnoreCase))
            {
                return ValidationResult.Failure($"ModelWhitelist entry '{model}' uses the unbounded 'auto' family, which is forbidden (CLAUDE.md, doc 05).");
            }

            if (!model.Contains(':'))
            {
                return ValidationResult.Failure($"ModelWhitelist entry '{model}' must be provider-qualified with ':' (e.g. 'anthropic:haiku-5.5').");
            }
        }

        // Validate tool allowlist
        if (manifest.ToolAllowlist != null)
        {
            foreach (var tool in manifest.ToolAllowlist)
            {
                if (string.IsNullOrWhiteSpace(tool) || !_toolNameRegex.IsMatch(tool))
                {
                    return ValidationResult.Failure($"Invalid ToolAllowlist entry '{tool}': must be alphanumeric with '.' or '-' up to 64 characters.");
                }
            }
        }

        return ValidationResult.Success;
    }
}
