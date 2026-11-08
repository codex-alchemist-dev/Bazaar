using Bazaar.Server.Vault.Git;

namespace Bazaar.Server.Vault.Writes;

/// <summary>
/// The conflict card doc 18 posts to a task's GC when an agent branch overlaps the main line (doc 18, Write path step 5).
/// It carries both sides of every conflicting document so a reviewer can see the choice, and the actions from
/// <c>vault.conflict_card_actions</c>. Posting the card into a GC is the caller's job; this type only builds it.
/// </summary>
public sealed record VaultConflictCard(
    string Branch,
    string BaseCommit,
    string MainCommit,
    IReadOnlyList<VaultConflictFile> Files,
    IReadOnlyList<string> Actions);

/// <summary>One conflicting document: the text at the base, on the main line, and on the agent branch.</summary>
public sealed record VaultConflictFile(string Path, string? Base, string? Main, string? Branch);

/// <summary>Builds <see cref="VaultConflictCard"/> values from git state.</summary>
public static class VaultConflictCards
{
    /// <summary>The shipped default for <c>vault.conflict_card_actions</c> (doc 18, settings table).</summary>
    public static IReadOnlyList<string> DefaultActions { get; } = ["keep mine", "keep theirs", "agent rebases"];

    /// <summary>
    /// Reads both sides of each conflicting path. A path missing on one side (added or deleted) reads as null there.
    /// </summary>
    public static async Task<VaultConflictCard> BuildAsync(
        IGitProvider git,
        string repoPath,
        string baseCommit,
        string mainCommit,
        string branch,
        IEnumerable<string> conflictPaths,
        IReadOnlyList<string>? actions,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        var files = new List<VaultConflictFile>();
        foreach (var path in conflictPaths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            var baseText = await ReadOrNullAsync(git, repoPath, baseCommit, path, ct);
            var mainText = await ReadOrNullAsync(git, repoPath, mainCommit, path, ct);
            var branchText = await ReadOrNullAsync(git, repoPath, branch, path, ct);
            files.Add(new VaultConflictFile(path, baseText, mainText, branchText));
        }

        return new VaultConflictCard(branch, baseCommit, mainCommit, files, actions ?? DefaultActions);
    }

    private static async Task<string?> ReadOrNullAsync(IGitProvider git, string repoPath, string commitish, string path, CancellationToken ct)
    {
        try
        {
            return await git.ReadTextAsync(repoPath, commitish, path, ct);
        }
        catch (FileNotFoundException)
        {
            // The path does not exist at this commit (added or deleted on one side).
            return null;
        }
    }
}
