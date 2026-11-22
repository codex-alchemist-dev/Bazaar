using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// Versioned mapping rules (doc 44 section 8.1 stage 5 and section 9). The hash covers the version and every option in
/// key order, so a change to either changes the mapping hash of a dry-run.
/// </summary>
public sealed record MappingRules(string Version, IReadOnlyDictionary<string, string> Options)
{
    public static MappingRules Default { get; } = new("1.0.0", new Dictionary<string, string>());

    public string Hash()
    {
        var builder = new StringBuilder(Version);
        foreach (var (key, value) in Options.OrderBy(o => o.Key, StringComparer.Ordinal))
        {
            builder.Append('\0').Append(key).Append('=').Append(value);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

/// <summary>A proposed object: where the record would land, and why it was dropped when it would not.</summary>
public sealed record MappingResult(ImportTargetKind? Target, SourceRecord? Record, string? SkipReason = null)
{
    public bool IsSkipped => Target is null;

    public static MappingResult To(ImportTargetKind target, SourceRecord record) => new(target, record);

    public static MappingResult Skip(string reason) => new(null, null, reason);
}

/// <summary>What a module may read and propose to write. Shown at install-time approval (doc 42). Never network or secrets.</summary>
public sealed record ImporterDeclaration(string FormatId, IReadOnlyList<ImportTargetKind> Targets)
{
    /// <summary>Importers read the staging input and nothing else.</summary>
    public IReadOnlyList<string> Reads { get; } = ["staging-input"];
}

/// <summary>Outcome of a module self-test: parse a built-in fixture and check the result.</summary>
public sealed record ImporterSelfTestResult(bool Ok, string? Problem = null);

/// <summary>
/// The doc 44 section 9 importer contract: format id, declaration, pure <see cref="Map"/>, and a self-test for the
/// health contract (doc 46). It extends <see cref="IPortabilityImporter"/>, so existing importers keep working and the
/// pipeline can use either shape. Verify is the shared <see cref="ImportVerifier"/>. This follows the PROPOSED sketch in
/// doc 44 section 9, because docs 42 and 46 are not written yet.
/// </summary>
public interface IMappingImporter : IPortabilityImporter
{
    /// <summary>The format this importer reads, for example "trello.json". Recorded in provenance with the version.</summary>
    string FormatId { get; }

    ImporterDeclaration Declaration { get; }

    /// <summary>Pure, no I/O: the same record and rules always give the same result.</summary>
    MappingResult Map(SourceRecord record, MappingRules rules);

    /// <summary>Parses a built-in fixture and checks it. Used by liveness and readiness checks.</summary>
    ImporterSelfTestResult SelfTest();
}

/// <summary>Shared mapping used by importers that have no format-specific rules: kind decides the target.</summary>
public static class DefaultMapping
{
    public static MappingResult Map(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return MappingResult.To(ImportCommitter.TargetFor(record), record);
    }
}

/// <summary>Checks any importer module must pass: pure mapping, a hashed rules version and a passing self-test.</summary>
public static class ImporterContractChecks
{
    public static IReadOnlyList<string> Check(IMappingImporter importer, ImportSource fixture)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(fixture);

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(importer.Id)) problems.Add("empty id");
        if (string.IsNullOrWhiteSpace(importer.Version)) problems.Add("empty version");
        if (string.IsNullOrWhiteSpace(importer.FormatId)) problems.Add("empty format id");
        if (importer.Declaration.Targets.Count == 0) problems.Add("declares no target");

        try
        {
            var rules = MappingRules.Default;
            foreach (var record in importer.Parse(fixture))
            {
                var first = importer.Map(record, rules);
                var second = importer.Map(record, rules);
                if (first != second) problems.Add("Map is not deterministic for " + record.SourceKey);
                if (first.Target is { } target && !importer.Declaration.Targets.Contains(target))
                {
                    problems.Add("Map proposes an undeclared target for " + record.SourceKey);
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add("fixture failed: " + ex.Message);
        }

        var self = importer.SelfTest();
        if (!self.Ok) problems.Add("self-test failed: " + self.Problem);
        return problems;
    }
}
