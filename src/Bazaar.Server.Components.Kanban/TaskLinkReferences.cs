using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Kanban;

/// <summary>
/// Shape checks for task link references (doc 12, Links), run before anything is written. They are pure: no I/O and no
/// lookups. GC existence is checked separately through <see cref="IGcExistence"/>, and PR and commit provider checks are
/// left to the link service.
/// <para>
/// A PR reference is stored as <c>owner/repo#number</c>, the form <see cref="TaskService.PrMergedAsync"/> matches exactly, so
/// this check accepts only the canonical form (the number has no leading zeros).
/// </para>
/// </summary>
public static partial class TaskLinkReferences
{
    private const int _maxVaultPathLength = 1024;

    // The patterns end in \z, not $: $ also matches before a trailing newline, which would let "abcdef1\n" through.

    // GitHub user and organisation names: 1 to 39 ASCII letters, digits or single hyphens, no leading or trailing hyphen.
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubOwnerRegex();

    // GitHub repository names: 1 to 100 ASCII letters, digits, hyphens, underscores or periods.
    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRepoRegex();

    [GeneratedRegex("^([^/#\\s]+)/([^/#\\s]+)#([1-9][0-9]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PrRefRegex();

    [GeneratedRegex("^[0-9a-f]{7,40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitRegex();

    /// <summary>Returns null when <paramref name="reference"/> is well formed for <paramref name="kind"/>, or a short reason when it is not.</summary>
    public static string? Check(TaskLinkKind kind, string? reference) => kind switch
    {
        TaskLinkKind.Pr => CheckPr(reference),
        TaskLinkKind.Commit => CheckCommit(reference),
        TaskLinkKind.Gc => CheckGc(reference),
        TaskLinkKind.VaultDoc => CheckVaultDoc(reference),
        _ => "Not a task link kind.",
    };

    /// <summary>The canonical PR reference: <c>owner/repo#number</c>, GitHub name rules, positive integer number with no leading zeros.</summary>
    private static string? CheckPr(string? reference)
    {
        if (reference is null)
        {
            return "A pull request reference is required.";
        }

        var match = PrRefRegex().Match(reference);
        if (!match.Success)
        {
            return "A pull request reference must be owner/repo#number.";
        }

        if (!GitHubOwnerRegex().IsMatch(match.Groups[1].Value))
        {
            return "The owner is not a valid GitHub name.";
        }

        var repo = match.Groups[2].Value;
        if (repo is "." or ".." || !GitHubRepoRegex().IsMatch(repo))
        {
            return "The repository is not a valid GitHub name.";
        }

        return long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? null
            : "The pull request number is too large.";
    }

    /// <summary>A lowercase hexadecimal commit SHA of 7 to 40 characters.</summary>
    private static string? CheckCommit(string? reference) =>
        reference is not null && CommitRegex().IsMatch(reference)
            ? null
            : "A commit must be 7 to 40 lowercase hexadecimal characters.";

    /// <summary>A GC id in the canonical lowercase hyphenated form. Existence is checked separately.</summary>
    private static string? CheckGc(string? reference)
    {
        if (reference is null || !Guid.TryParseExact(reference, "D", out var id))
        {
            return "A GC reference must be a GUID.";
        }

        return id.ToString("D", CultureInfo.InvariantCulture) == reference
            ? null
            : "A GC reference must be a GUID in lowercase canonical form.";
    }

    /// <summary>
    /// A vault-relative Markdown path: no leading slash, no backslashes, no control characters, no empty or dot segments
    /// (so no <c>..</c>), and it ends in <c>.md</c> after a non-empty name. The file is not read.
    /// </summary>
    private static string? CheckVaultDoc(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return "A vault document path is required.";
        }

        if (reference.Length > _maxVaultPathLength)
        {
            return $"A vault document path may be at most {_maxVaultPathLength} characters.";
        }

        if (reference.Any(char.IsControl))
        {
            return "A vault document path must not contain control characters.";
        }

        if (reference[0] == '/' || reference.Contains('\\'))
        {
            return "A vault document path must be relative and use forward slashes.";
        }

        var segments = reference.Split('/');
        if (segments.Any(s => s is "" or "." or ".."))
        {
            return "A vault document path must not contain empty, '.' or '..' segments.";
        }

        var name = segments[^1];
        return name.Length > ".md".Length && name.EndsWith(".md", StringComparison.Ordinal)
            ? null
            : "A vault document path must end in .md.";
    }
}
