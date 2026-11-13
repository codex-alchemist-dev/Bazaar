namespace Bazaar.Server.Knowledge.Talent;

/// <summary>
/// A Soul template manifest offered to the Talent Pool (docs/design/25-company-systems-knowledge.md §2).
/// Contains only behavioural intent: name, short role description, prompt, tool allowlist, suggested model whitelist, and specialties.
/// Holds NO memory notes, private files, or personal data.
/// </summary>
/// <param name="TemplateId">Stable slug identifying the template (e.g. "senior-code-reviewer").</param>
/// <param name="Name">Display name of the template.</param>
/// <param name="Role">Short role description.</param>
/// <param name="Prompt">System charter prompt describing the agent's behavior.</param>
/// <param name="ToolAllowlist">Allowlisted tools this template may invoke.</param>
/// <param name="ModelWhitelist">Suggested provider-qualified models (e.g. "anthropic:haiku-5.5").</param>
/// <param name="Specialties">Tags describing capabilities or domains.</param>
public sealed record TalentManifest(
    string TemplateId,
    string Name,
    string Role,
    string Prompt,
    IReadOnlyList<string> ToolAllowlist,
    IReadOnlyList<string> ModelWhitelist,
    IReadOnlyList<string> Specialties);
