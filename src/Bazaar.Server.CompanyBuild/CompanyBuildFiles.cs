using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>The plain files a company build leaves beside its outputs (docs/design/37 section 4a).</summary>
public static class CompanyBuildFiles
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Writes <c>SHA256SUMS</c> for every output file under the company folder, skipping the pipeline's own files.</summary>
    public static async Task WriteChecksumsAsync(CompanyBuildOutputLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var builder = new StringBuilder();
        var files = Directory.Exists(layout.CompanyDirectory)
            ? Directory.EnumerateFiles(layout.CompanyDirectory, "*", SearchOption.AllDirectories)
            : [];
        var relative = files
            .Select(path => Path.GetRelativePath(layout.CompanyDirectory, path).Replace('\\', '/'))
            .Where(path => !CompanyBuildOutputLayout.ReservedFileNames.Contains(path, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal);

        foreach (var path in relative)
        {
            await using var stream = File.OpenRead(Path.Combine(layout.CompanyDirectory, path));
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            builder.Append(Convert.ToHexStringLower(hash)).Append("  ").Append(path).Append('\n');
        }

        WriteAtomic(layout.ChecksumsPath, builder.ToString());
    }

    /// <summary>Renders README.txt. An unsigned set is labelled as such, as doc 37 section 4a requires.</summary>
    public static string RenderReadme(
        CompanyBuildPlan plan,
        CompanyBuildSetReport report,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? hostNotes = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();
        text.Append("Company build for ").Append(report.CompanyId).Append('\n');
        text.Append(report.IsSigned
            ? "Signing: configured. Outputs are signed with the company's signing settings.\n"
            : "UNSIGNED BUILD: no signing was configured. Outputs are unsigned and the OS may warn when installing them.\n");
        text.Append(report.Complete
            ? "Status: complete.\n"
            : "Status: INCOMPLETE. At least one target failed. See BUILD-REPORT.txt. Do not distribute this folder as a finished build.\n");
        text.Append("Modules (start order): ").Append(string.Join(", ", plan.Modules.Select(m => m.Id))).Append('\n');
        if (plan.AddedDependencies.Count > 0)
        {
            text.Append("Added as hard dependencies: ").Append(string.Join(", ", plan.AddedDependencies)).Append('\n');
        }

        if (hostNotes is not null)
        {
            foreach (var (targetId, notes) in hostNotes.Where(n => n.Value.Count > 0).OrderBy(n => n.Key, StringComparer.Ordinal))
            {
                text.Append("Client modules that will not load on ").Append(targetId).Append(": ").Append(string.Join("; ", notes)).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Renders BUILD-REPORT.txt: every target, its attempts, its retries and each failure message.</summary>
    public static string RenderReport(CompanyBuildSetReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var text = new StringBuilder();
        text.Append("Company: ").Append(report.CompanyId).Append('\n');
        text.Append("Finished: ").Append(report.FinishedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Complete: ").Append(report.Complete ? "yes" : "no").Append('\n');
        foreach (var target in report.Targets)
        {
            var r = target.Report;
            text.Append('\n').Append("Target ").Append(target.TargetId).Append(": ").Append(r.Succeeded ? "succeeded" : "FAILED").Append('\n');
            text.Append("  Attempts: ").Append(r.Attempts).Append('\n');
            text.Append("  Retries: ").Append(r.Retries).Append('\n');
            foreach (var message in r.FailureMessages)
            {
                text.Append("  - ").Append(message).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Appends one JSON line to <c>build-history.jsonl</c>. The history is kept across rebuilds.</summary>
    public static void AppendHistory(CompanyBuildOutputLayout layout, CompanyBuildSetReport report)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(report);

        var entry = new CompanyBuildHistoryEntry(
            report.CompanyId,
            report.FinishedAt,
            report.Complete,
            report.IsSigned,
            [.. report.Targets.Select(t => new CompanyBuildHistoryTarget(t.TargetId, t.Report.Succeeded, t.Report.Attempts, t.Report.Retries))]);
        File.AppendAllText(
            layout.HistoryPath,
            JsonSerializer.Serialize(entry, CompanyBuildJsonContext.Default.CompanyBuildHistoryEntry) + "\n",
            _utf8NoBom);
    }

    /// <summary>Writes a file through a temp file and a rename, so a reader never sees a half-written file.</summary>
    public static void WriteAtomic(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, _utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>One line of <c>build-history.jsonl</c>.</summary>
public sealed record CompanyBuildHistoryEntry(
    string CompanyId,
    DateTimeOffset FinishedAt,
    bool Complete,
    bool IsSigned,
    IReadOnlyList<CompanyBuildHistoryTarget> Targets);

/// <summary>One target within a history line.</summary>
public sealed record CompanyBuildHistoryTarget(string TargetId, bool Succeeded, int Attempts, int Retries);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CompanyBuildHistoryEntry))]
internal sealed partial class CompanyBuildJsonContext : JsonSerializerContext;
