namespace Bazaar.Server.TalentPool;

/// <summary>Size limits for a manifest (docs/design/25 asks for over-size fields to be refused; the numbers are this module's defaults).</summary>
public static class TalentManifestLimits
{
    public const int MaxPromptBytes = 64 * 1024;
    public const int MaxTools = 128;
    public const int MaxModels = 32;
    public const int MaxNameLength = 128;
}

/// <summary>Stable error codes of <see cref="TalentManifestValidator"/>.</summary>
public static class TalentManifestErrorCodes
{
    public const string PromptEmpty = "prompt_empty";
    public const string PromptTooLarge = "prompt_too_large";
    public const string MemoryNotAllowed = "memory_not_allowed";
    public const string UnexpectedContent = "unexpected_content";
    public const string ModelsEmpty = "models_empty";
    public const string ModelNotWhitelisted = "model_not_whitelisted";
    public const string TooManyTools = "too_many_tools";
    public const string TooManyModels = "too_many_models";
    public const string BadName = "bad_name";
}

/// <summary>The outcome of validating one manifest. <see cref="Validated"/> is set exactly when <see cref="Errors"/> is empty.</summary>
public sealed record TalentManifestValidation(ValidatedTalentManifest? Validated, IReadOnlyList<TalentManifestError> Errors)
{
    public bool IsValid => Validated is not null;
}

/// <summary>
/// Pure manifest checks (docs/design/25, Testing): a template holds a prompt, a tool allowlist and a model whitelist, never memory. No I/O.
/// Every problem is reported, not just the first. The result carries the canonical manifest and its deterministic content hash.
/// </summary>
public static class TalentManifestValidator
{
    /// <param name="manifest">The submitted manifest.</param>
    /// <param name="installModelWhitelist">Models the submitting actor may use. Every model the template names must be in it (ordinal).</param>
    public static TalentManifestValidation Validate(TalentManifest manifest, IReadOnlySet<string> installModelWhitelist)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(installModelWhitelist);
        var errors = new List<TalentManifestError>();

        var prompt = (manifest.Prompt ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(prompt))
            errors.Add(new(TalentManifestErrorCodes.PromptEmpty, "The prompt is empty."));
        else if (System.Text.Encoding.UTF8.GetByteCount(prompt) > TalentManifestLimits.MaxPromptBytes)
            errors.Add(new(TalentManifestErrorCodes.PromptTooLarge, $"The prompt is larger than {TalentManifestLimits.MaxPromptBytes} bytes."));

        // Anything beyond the three manifest parts is refused. Memory gets its own code because it is the case the design names.
        foreach (var name in (manifest.Files ?? []).Concat(manifest.ExtraFields ?? []))
        {
            errors.Add(MentionsMemory(name)
                ? new(TalentManifestErrorCodes.MemoryNotAllowed, $"'{name}' looks like memory. A template carries no memory.")
                : new(TalentManifestErrorCodes.UnexpectedContent, $"'{name}' is not part of a template. Only a prompt, tools and models are."));
        }

        var tools = Names(manifest.ToolAllowlist, "tool", TalentManifestLimits.MaxTools, TalentManifestErrorCodes.TooManyTools, errors);
        var models = Names(manifest.ModelWhitelist, "model", TalentManifestLimits.MaxModels, TalentManifestErrorCodes.TooManyModels, errors);
        if ((manifest.ModelWhitelist?.Count ?? 0) == 0)
            errors.Add(new(TalentManifestErrorCodes.ModelsEmpty, "The template names no model."));
        foreach (var model in models.Where(m => !installModelWhitelist.Contains(m)))
            errors.Add(new(TalentManifestErrorCodes.ModelNotWhitelisted, $"Model '{model}' is not on the whitelist."));

        if (errors.Count > 0)
            return new TalentManifestValidation(null, errors);

        var canonical = new TalentManifest(prompt, tools, models, manifest.TemplateId, [], []);
        return new TalentManifestValidation(new ValidatedTalentManifest(canonical, TalentManifestJson.Sha(canonical)), []);
    }

    /// <summary>True when the name contains a memory marker (<c>memory</c>, <c>memories</c>, <c>MEMORY.md</c>, <c>mem/</c> paths).</summary>
    internal static bool MentionsMemory(string name) =>
        name.Contains("memor", StringComparison.OrdinalIgnoreCase)
        || name.Split('/', '\\', '.').Any(part => part.Equals("mem", StringComparison.OrdinalIgnoreCase));

    private static List<string> Names(IReadOnlyList<string>? input, string kind, int max, string tooManyCode, List<TalentManifestError> errors)
    {
        var result = new List<string>();
        if (input is null)
            return result;
        if (input.Count > max)
            errors.Add(new(tooManyCode, $"More than {max} {kind}s."));

        foreach (var name in input)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > TalentManifestLimits.MaxNameLength || name.Any(char.IsControl))
                errors.Add(new(TalentManifestErrorCodes.BadName, $"A {kind} name is blank, padded, has control characters or is over {TalentManifestLimits.MaxNameLength} characters."));
            else
                result.Add(name);
        }

        return [.. result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}
