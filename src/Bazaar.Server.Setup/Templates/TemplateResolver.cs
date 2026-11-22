
namespace Bazaar.Server.Setup;

/// <summary>
/// The outcome of resolving a plan's <c>company.source</c> (doc 45 section 1.8). A blank or non-template source resolves to nothing.
/// The fields that identify the template (<see cref="TemplateId"/>, <see cref="TemplateVersion"/>, <see cref="TemplateHash"/>) are
/// what the apply row records.
/// </summary>
/// <param name="TemplateId">The template id from the source, or null when the source names no template.</param>
/// <param name="TemplateVersion">The exact version from the source, or null.</param>
/// <param name="Template">The loaded template, or null when there is none or it has an error.</param>
/// <param name="TemplateHash">The template hash, or null when there is no template.</param>
/// <param name="Seeds">The seed bytes the template names, keyed by relative path. Empty when there is no template.</param>
/// <param name="Issues">Every error and hold. Paths name keys (for template problems, relative to the template's <c>template.json</c>).</param>
public sealed record TemplateResolution(
    string? TemplateId,
    string? TemplateVersion,
    SetupTemplate? Template,
    string? TemplateHash,
    IReadOnlyDictionary<string, byte[]> Seeds,
    IReadOnlyList<SetupValidationIssue> Issues)
{
    /// <summary>The exit code for this resolution: 0 valid, 1 invalid input, 2 held (doc 45 section 1.7).</summary>
    public SetupExitCode ExitCode => SetupExitCodes.ForIssues(Issues);
}

/// <summary>
/// Turns <c>company.source = template:&lt;id&gt;@&lt;semver&gt;</c> into a loaded template (doc 45 section 1.8), through an injected
/// <see cref="ITemplateCatalog"/>. Lookups are exact: the id and the full version must both match. A version range is refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>One company per install.</b> A template is an input to the setup plan. It never creates a second company: the company
/// block of the plan names the one company, and the template contributes settings presets, a module list and seeds only.
/// </para>
/// <para>
/// <b>Overlay.</b> When the template loads with no errors, its settings presets and its module list are merged into the plan in
/// place (<see cref="TemplateOverlay"/>). A value the plan already sets wins over the template's value. Modules are a set, so the
/// merged list is the union, sorted. The plan hash must be computed after this call, because the merged plan is a different plan.
/// </para>
/// </remarks>
public sealed class TemplateResolver
{
    private readonly ITemplateCatalog _catalog;
    private readonly ISetupSettingCatalog _registry;

    /// <param name="catalog">Where exact template versions come from.</param>
    /// <param name="registry">The settings registry the template's presets are checked against.</param>
    public TemplateResolver(ITemplateCatalog catalog, ISetupSettingCatalog registry)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Resolves <see cref="SetupPlan.Company"/>'s source. Returns an error for a malformed source, a version range, a missing
    /// version, or a catalog entry that does not hold the template it names. Merges the overlay into <paramref name="plan"/> only when
    /// the template loads with no errors.
    /// </summary>
    public async Task<TemplateResolution> ResolveAsync(SetupPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var source = plan.Company.Source ?? string.Empty;
        if (!source.StartsWith("template:", StringComparison.Ordinal))
        {
            // blank and import sources name no template. Import is a later slice (doc 44).
            return NoTemplate([]);
        }

        var rest = source["template:".Length..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return Fail(MalformedSource());
        }

        var id = rest[..at];
        var version = rest[(at + 1)..];
        if (!SetupPlanValidator.IsTemplateId(id))
        {
            return Fail(MalformedSource());
        }

        if (!SetupPlanValidator.IsSemver(version))
        {
            return Fail(new SetupValidationIssue(
                SetupIssueSeverity.Error,
                "company.source",
                "must name one exact version, such as template:starter@1.0.0. Version ranges are not supported."));
        }

        ITemplateSource? templateSource;
        try
        {
            templateSource = await _catalog.FindAsync(id, version, cancellationToken);
        }
        catch (TemplateSourceException ex)
        {
            return Fail(new SetupValidationIssue(SetupIssueSeverity.Error, "company.source", ex.Message));
        }

        if (templateSource is null)
        {
            return Fail(new SetupValidationIssue(
                SetupIssueSeverity.Error,
                "company.source",
                "names a template version that this install does not hold. Check the id and the exact version."));
        }

        var loaded = await TemplateLoader.LoadAsync(templateSource, _registry, cancellationToken);
        var issues = loaded.Issues.ToList();
        if (loaded.Template is null || loaded.TemplateHash is null)
        {
            return new TemplateResolution(id, version, null, null, loaded.Seeds, issues);
        }

        if (!string.Equals(loaded.Template.Id, id, StringComparison.Ordinal) || !string.Equals(loaded.Template.Version, version, StringComparison.Ordinal))
        {
            issues.Add(new SetupValidationIssue(
                SetupIssueSeverity.Error,
                "company.source",
                "the catalog returned a template that is not the one it was asked for."));
            return new TemplateResolution(id, version, null, null, loaded.Seeds, issues);
        }

        if (_catalog is IPinnedTemplateCatalog pinned
            && pinned.GetPinnedHash(id, version) is { } expected
            && !string.Equals(expected, loaded.TemplateHash, StringComparison.Ordinal))
        {
            issues.Add(new SetupValidationIssue(
                SetupIssueSeverity.Error,
                "company.source",
                "names a template whose content does not match the template hash pinned in the catalog."));
            return new TemplateResolution(id, version, null, null, loaded.Seeds, issues);
        }

        if (SetupExitCodes.ForIssues(issues) == SetupExitCode.InvalidInput)
        {
            return new TemplateResolution(id, version, null, null, loaded.Seeds, issues);
        }

        TemplateOverlay.Apply(plan, loaded.Template);
        return new TemplateResolution(id, version, loaded.Template, loaded.TemplateHash, loaded.Seeds, issues);
    }

    private static SetupValidationIssue MalformedSource() => new(
        SetupIssueSeverity.Error,
        "company.source",
        "template sources must be template:<id>@<semver>, for example template:starter@1.0.0.");

    private static TemplateResolution NoTemplate(List<SetupValidationIssue> issues) =>
        new(null, null, null, null, new Dictionary<string, byte[]>(StringComparer.Ordinal), issues);

    private static TemplateResolution Fail(SetupValidationIssue issue) =>
        NoTemplate([issue]);
}

/// <summary>
/// Merges a template's plan-level changes into a plan (doc 45 section 1.8). Settings presets are added where the plan does not set
/// the key, so the plan's value wins on conflict. The module list is the union of the template's and the plan's, sorted, because a
/// template cannot remove a module the plan names. The plan's validator checks the merged result again, as for any plan.
/// </summary>
public static class TemplateOverlay
{
    /// <summary>Merges <paramref name="template"/> into <paramref name="plan"/> in place.</summary>
    public static void Apply(SetupPlan plan, SetupTemplate template)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(template);

        foreach (var (name, value) in template.Settings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!plan.Settings.ContainsKey(name))
            {
                plan.Settings[name] = value.Clone();
            }
        }

        var modules = plan.Modules.Concat(template.Modules).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
        plan.Modules.Clear();
        plan.Modules.AddRange(modules);
    }
}
