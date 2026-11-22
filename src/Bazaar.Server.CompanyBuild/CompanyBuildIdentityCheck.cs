using System.Text.Json;
using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The setup check "the server's identity and key hash match the identity baked into the company build" (docs/design/45
/// section 1.4, checks list; section 2.1). It compares the server's own address and key-hash pin with the identity the most
/// recent build recorded in <c>build-history.jsonl</c>. A client built for another address or key would fail closed at connect time,
/// so a mismatch is reported before anyone installs it.
/// </summary>
public static class CompanyBuildIdentityCheck
{
    /// <summary>Reads the newest line of <c>build-history.jsonl</c>, or null when the company has no build or the line is unreadable.</summary>
    public static CompanyBuildHistoryEntry? ReadLastBuild(CompanyBuildOutputLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!File.Exists(layout.HistoryPath))
        {
            return null;
        }

        var last = File.ReadLines(layout.HistoryPath).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (last is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(last, CompanyBuildJsonContext.Default.CompanyBuildHistoryEntry);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Compares the server's identity with the last build. An empty list means the build matches this server.</summary>
    public static IReadOnlyList<CompanyBuildIdentityIssue> Check(CompanyBuildHistoryEntry? lastBuild, Uri serverAddress, string serverKeyHashPin)
    {
        ArgumentNullException.ThrowIfNull(serverAddress);
        ArgumentNullException.ThrowIfNull(serverKeyHashPin);

        if (lastBuild is null)
        {
            return [new CompanyBuildIdentityIssue("no-build", "No company build has been recorded yet.")];
        }

        var issues = new List<CompanyBuildIdentityIssue>();
        if (!lastBuild.Complete)
        {
            issues.Add(new CompanyBuildIdentityIssue("build-incomplete", "The last company build did not complete."));
        }

        if (lastBuild.Address is null || lastBuild.KeyHashPin is null)
        {
            issues.Add(new CompanyBuildIdentityIssue("identity-unrecorded", "The last build did not record its server identity."));
            return issues;
        }

        if (!string.Equals(lastBuild.Address, serverAddress.AbsoluteUri, StringComparison.Ordinal))
        {
            issues.Add(new CompanyBuildIdentityIssue("address-mismatch", $"The last build points at {lastBuild.Address}; this server is {serverAddress.AbsoluteUri}."));
        }

        if (!string.Equals(lastBuild.KeyHashPin, serverKeyHashPin, StringComparison.Ordinal))
        {
            issues.Add(new CompanyBuildIdentityIssue("key-pin-mismatch", "The last build pins a different server key than this server holds."));
        }

        return issues;
    }
}
