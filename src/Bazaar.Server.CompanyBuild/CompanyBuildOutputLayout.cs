using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// Where a company's build lands (docs/design/37 section 4a): one output folder per company, one subfolder per target, plus the
/// checksum file, README, plain report, build history and an incomplete marker. Ids become path segments, so they are validated
/// to a short, safe alphabet and can never climb out of the output root.
/// </summary>
public sealed class CompanyBuildOutputLayout
{
    /// <summary>File names the pipeline writes itself. They are never hashed into <c>SHA256SUMS</c>.</summary>
    public static IReadOnlyList<string> ReservedFileNames { get; } =
        ["SHA256SUMS", "README.txt", "BUILD-REPORT.txt", "build-history.jsonl", "INCOMPLETE"];

    public CompanyBuildOutputLayout(string outputRoot, string companyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        CompanyDirectory = Path.Combine(Path.GetFullPath(outputRoot), ValidateSegment(companyId, "company id"));
    }

    /// <summary>The company's output folder, for example <c>out/CDAX</c>.</summary>
    public string CompanyDirectory { get; }

    public string ChecksumsPath => Path.Combine(CompanyDirectory, "SHA256SUMS");

    public string ReadmePath => Path.Combine(CompanyDirectory, "README.txt");

    public string ReportPath => Path.Combine(CompanyDirectory, "BUILD-REPORT.txt");

    public string HistoryPath => Path.Combine(CompanyDirectory, "build-history.jsonl");

    public string IncompleteMarkerPath => Path.Combine(CompanyDirectory, "INCOMPLETE");

    /// <summary>The folder one target builds into.</summary>
    /// <exception cref="CompanyBuildException">The id is empty, too long, or has characters outside letters, digits, dot, dash and underscore.</exception>
    public string TargetDirectory(string targetId) =>
        Path.Combine(CompanyDirectory, ValidateSegment(targetId, "target id"));

    internal static string ValidateSegment(string value, string what)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value is "." or ".." || value.StartsWith('.'))
        {
            throw new CompanyBuildException($"The {what} '{value}' is not a valid folder name.");
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                throw new CompanyBuildException($"The {what} '{value}' may use only letters, digits, dot, dash and underscore.");
            }
        }

        if (ReservedFileNames.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            throw new CompanyBuildException($"The {what} '{value}' is reserved.");
        }

        return value;
    }
}
