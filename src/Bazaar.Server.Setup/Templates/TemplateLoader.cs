using System.Text;
using System.Text.Json;
using Bazaar.Server.Settings;

namespace Bazaar.Server.Setup;

/// <summary>
/// The result of loading a template: the normalized template, its hash and its seed bytes when there are no errors, plus every issue
/// found. Holds (for example a setting that needs approval) do not stop the load, because the plan decides whether to hold.
/// </summary>
/// <param name="Template">The normalized template. Null when the template has an error, so nothing downstream can use a broken one.</param>
/// <param name="TemplateHash">The template hash (see <see cref="TemplateHash"/>). Null exactly when <see cref="Template"/> is null.</param>
/// <param name="Seeds">The bytes of each seed the template names, keyed by relative path. Empty when the template has an error.</param>
/// <param name="Issues">Every error and hold. Paths name keys in <c>template.json</c> (or the seed key), never values.</param>
public sealed record TemplateLoadResult(
    SetupTemplate? Template,
    string? TemplateHash,
    IReadOnlyDictionary<string, byte[]> Seeds,
    IReadOnlyList<SetupValidationIssue> Issues)
{
    /// <summary>The exit code for this load: 0 valid, 1 invalid input, 2 held (doc 45 section 1.7).</summary>
    public SetupExitCode ExitCode => SetupExitCodes.ForIssues(Issues);
}

/// <summary>
/// Reads and checks one template version (doc 45 section 1.8) through an <see cref="ITemplateSource"/>. The steps: read
/// <c>template.json</c> within its size limit, parse it strictly (UTF-8, no comments, no trailing commas, no duplicate keys), walk it
/// for unknown keys, nulls, the never-contains deny-list and secret references, decode it, run the semantic rules, read the seeds
/// within their limits, and compute the hash. A template with an error is never returned. Git fetching is not part of this step;
/// a catalog supplies the source.
/// </summary>
public static class TemplateLoader
{
    /// <summary>Loads the template in <paramref name="source"/>. <paramref name="registry"/> defaults to the registry this build ships.</summary>
    public static async Task<TemplateLoadResult> LoadAsync(
        ITemplateSource source,
        SettingRegistry? registry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        registry ??= SettingRegistry.Known;

        var issues = new List<SetupValidationIssue>();
        var manifest = await ReadFileAsync(source, SetupTemplateConstants.ManifestFileName, SetupTemplateConstants.MaxManifestBytes, SetupTemplateConstants.ManifestFileName, issues, cancellationToken);
        if (manifest is null)
        {
            return Failed(issues);
        }

        var template = ParseManifest(manifest, issues);
        if (template is null)
        {
            return Failed(issues);
        }

        var references = TemplateValidator.Validate(template, registry, issues);
        if (issues.Any(issue => issue.Severity == SetupIssueSeverity.Error))
        {
            return Failed(issues);
        }

        var seeds = await ReadSeedsAsync(source, references, manifest.Length, issues, cancellationToken);
        if (seeds is null)
        {
            return Failed(issues);
        }

        return new TemplateLoadResult(template, TemplateHash.Compute(template, seeds), seeds, issues);
    }

    private static TemplateLoadResult Failed(List<SetupValidationIssue> issues) =>
        new(null, null, new Dictionary<string, byte[]>(StringComparer.Ordinal), issues);

    /// <summary>Reads one file, reporting a missing, oversize or rejected file as an error at <paramref name="errorPath"/>.</summary>
    private static async Task<byte[]?> ReadFileAsync(
        ITemplateSource source,
        string relativePath,
        int maxBytes,
        string errorPath,
        List<SetupValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var read = await source.ReadAsync(relativePath, maxBytes, cancellationToken);
        if (read.Status == TemplateFileStatus.Found && read.Bytes is { } bytes)
        {
            if (bytes.Length <= maxBytes)
            {
                return bytes;
            }

            issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, errorPath, $"is larger than the limit of {maxBytes} bytes."));
            return null;
        }

        var message = read.Status switch
        {
            TemplateFileStatus.Missing => "is missing from the template folder.",
            TemplateFileStatus.TooLarge => $"is larger than the limit of {maxBytes} bytes.",
            _ => "could not be read: it is a link or lies outside the template folder.",
        };
        issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, errorPath, message));
        return null;
    }

    /// <summary>Parses and checks the manifest. Returns the decoded template, or null after adding the errors.</summary>
    private static SetupTemplate? ParseManifest(byte[] manifest, List<SetupValidationIssue> issues)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifest, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });
        }
        catch (JsonException ex)
        {
            // The line number is the only detail reported; the text of the file is never echoed.
            issues.Add(new SetupValidationIssue(
                SetupIssueSeverity.Error,
                "$",
                $"is not valid UTF-8 JSON (line {(ex.LineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")})."));
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, "$", "must hold one JSON object."));
                return null;
            }

            // The walk reports repeated, unknown, null and deny-listed keys and the missing required keys. The schema version
            // is checked by the validator once the document is decoded.
            TemplateDocumentWalker.Walk(root, issues);
            if (issues.Any(issue => issue.Severity == SetupIssueSeverity.Error))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(root.GetRawText(), SetupTemplateJsonContext.Default.SetupTemplate);
            }
            catch (JsonException ex)
            {
                // The path names the failing key. The value is never quoted.
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, ex.Path ?? "$", "has a value of the wrong JSON type."));
                return null;
            }
        }
    }

    /// <summary>
    /// Reads every named seed in path order, within the per-file and total limits, and checks each is UTF-8 text. Returns null after
    /// adding the errors.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>?> ReadSeedsAsync(
        ITemplateSource source,
        IReadOnlyList<TemplateSeedReference> references,
        long manifestBytes,
        List<SetupValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var seeds = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = manifestBytes;
        foreach (var reference in references)
        {
            var bytes = await ReadFileAsync(source, reference.RelativePath, SetupTemplateConstants.MaxSeedBytes, reference.KeyPath, issues, cancellationToken);
            if (bytes is null)
            {
                continue;
            }

            if (!IsUtf8(bytes))
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, reference.KeyPath, "is not valid UTF-8 text."));
                continue;
            }

            total += bytes.Length;
            if (total > SetupTemplateConstants.MaxTotalBytes)
            {
                issues.Add(new SetupValidationIssue(SetupIssueSeverity.Error, "seeds", $"together with template.json is larger than the total limit of {SetupTemplateConstants.MaxTotalBytes} bytes."));
                return null;
            }

            seeds[reference.RelativePath] = bytes;
        }

        return issues.Any(issue => issue.Severity == SetupIssueSeverity.Error) ? null : seeds;
    }

    /// <summary>True when the bytes decode as strict UTF-8 (no replacement characters are invented).</summary>
    private static bool IsUtf8(byte[] bytes)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
