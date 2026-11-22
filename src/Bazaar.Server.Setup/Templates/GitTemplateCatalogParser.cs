using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>The result of reading a git template catalog file: the entries when there are no errors, and every issue found.</summary>
/// <param name="Entries">The listed template versions. Empty when there is an error.</param>
/// <param name="Issues">Every error. Paths name keys, never values (a URL is not echoed).</param>
public sealed record GitTemplateCatalogParseResult(IReadOnlyList<GitTemplateEntry> Entries, IReadOnlyList<SetupValidationIssue> Issues)
{
    /// <summary>True when the file parsed with no errors.</summary>
    public bool IsValid => Issues.Count == 0;
}

/// <summary>
/// Reads the git template catalog file (doc 45 section 1.8): strict UTF-8 JSON, no comments, no trailing commas, no duplicate or
/// unknown keys, one exact schema version. Each entry is checked against <see cref="GitTemplateRules"/>, ids and versions use the
/// same rules as <c>company.source</c>, and the same (id, version) may be listed once.
/// </summary>
public static class GitTemplateCatalogParser
{
    /// <summary>The largest catalog file accepted, in bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>The most entries one catalog file may list.</summary>
    public const int MaxEntries = 256;

    /// <summary>Parses <paramref name="json"/>. <paramref name="allowedSchemes"/> is the operator's URL scheme list.</summary>
    public static GitTemplateCatalogParseResult Parse(ReadOnlySpan<byte> json, IReadOnlyList<string> allowedSchemes)
    {
        ArgumentNullException.ThrowIfNull(allowedSchemes);
        var issues = new List<SetupValidationIssue>();
        if (json.Length > MaxBytes)
        {
            return Fail(issues, "$", $"is larger than the limit of {MaxBytes} bytes.");
        }

        GitTemplateCatalogFile? file;
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                AllowDuplicateProperties = false,
            });
            file = JsonSerializer.Deserialize(document.RootElement.GetRawText(), GitTemplateCatalogJsonContext.Default.GitTemplateCatalogFile);
        }
        catch (JsonException ex)
        {
            // Only the key path and line are reported; no value is quoted.
            return Fail(issues, ex.Path ?? "$", "is not valid catalog JSON (check the keys and value types).");
        }

        if (file is null)
        {
            return Fail(issues, "$", "must hold one JSON object.");
        }

        if (file.SchemaVersion != GitTemplateCatalogFile.CurrentSchemaVersion)
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, "schemaVersion", $"must be {GitTemplateCatalogFile.CurrentSchemaVersion}."));
        }

        if (file.Templates.Count > MaxEntries)
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, "templates", $"lists more than {MaxEntries} entries."));
            return new GitTemplateCatalogParseResult([], issues);
        }

        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < file.Templates.Count; i++)
        {
            CheckEntry(file.Templates[i], $"templates[{i}]", allowedSchemes, issues);
            if (!seen.Add((file.Templates[i].Id, file.Templates[i].Version)))
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, $"templates[{i}]", "repeats an id and version listed earlier."));
            }
        }

        return issues.Count == 0 ? new GitTemplateCatalogParseResult(file.Templates, issues) : new GitTemplateCatalogParseResult([], issues);
    }

    /// <summary>Checks one entry and adds an error per failing key.</summary>
    internal static void CheckEntry(GitTemplateEntry entry, string path, IReadOnlyList<string> allowedSchemes, List<SetupValidationIssue> issues)
    {
        if (!SetupPlanValidator.IsTemplateId(entry.Id))
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path + ".id", "must be lower-case letters, digits and dashes."));
        }

        if (!SetupPlanValidator.IsSemver(entry.Version))
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path + ".version", "must be one exact semantic version."));
        }

        if (GitTemplateRules.CheckUrl(entry.Url, allowedSchemes) is { } urlProblem)
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path + ".url", urlProblem));
        }

        if (!GitTemplateRules.IsCommitId(entry.Commit))
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path + ".commit", "must be a full commit id in lower-case hex. Branch and tag names are not pinned."));
        }

        if (GitTemplateRules.CheckSubfolder(entry.Path) is { } pathProblem)
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path + ".path", pathProblem));
        }

        if (!GitTemplateRules.IsTemplateHash(entry.TemplateHash))
        {
            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path + ".templateHash", "must be the 64-character lower-case hex template hash."));
        }
    }

    private static GitTemplateCatalogParseResult Fail(List<SetupValidationIssue> issues, string path, string message)
    {
        issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, path, message));
        return new GitTemplateCatalogParseResult([], issues);
    }
}
