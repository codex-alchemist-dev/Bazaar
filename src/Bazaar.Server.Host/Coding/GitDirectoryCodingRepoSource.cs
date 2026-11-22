using System.Globalization;
using System.Text.RegularExpressions;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingWorkspace;
using Bazaar.Server.Git;
using CodingRepoRef = Bazaar.Server.CodingGit.RepoRef;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// <see cref="ICodingRepoSource"/> over the Git module's <see cref="IGitRepositoryDirectory"/> (doc 17, "Workspace sources"; doc 52).
/// Fails closed: anything that is not a usable GitHub repo with a usable local clone is null, never a guess.
/// </summary>
public sealed partial class GitDirectoryCodingRepoSource : ICodingRepoSource
{
    // The id becomes a path segment downstream, so Windows device names are refused like in the configured source.
    private static readonly HashSet<string> _deviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IGitRepositoryDirectory _directory;
    private readonly string _defaultBranch;
    private readonly string? _root;

    /// <summary>Creates the source.</summary>
    /// <param name="directory">The Git module's repository directory.</param>
    /// <param name="options">The work-share root, so a clone on or around it is refused.</param>
    /// <param name="defaultBranch">The default branch for every repo (the directory carries none).</param>
    /// <exception cref="InvalidOperationException">The default branch is invalid.</exception>
    public GitDirectoryCodingRepoSource(IGitRepositoryDirectory directory, CodingWorkspaceOptions options, string defaultBranch = "main")
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(defaultBranch) || defaultBranch.Any(char.IsControl) || defaultBranch.StartsWith('-'))
        {
            throw new InvalidOperationException("Coding:Workspace:DefaultBranch is not a valid branch name.");
        }

        _defaultBranch = defaultBranch;
        _root = string.IsNullOrWhiteSpace(options.Root) || !Path.IsPathFullyQualified(options.Root)
            ? null
            : WorkSharePaths.Canonicalize(options.Root);
    }

    /// <inheritdoc />
    public async ValueTask<CodingRepoRecord?> FindAsync(string repoId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IdPattern().IsMatch(repoId ?? string.Empty) || repoId!.EndsWith('.') || _deviceNames.Contains(repoId.Split('.')[0]))
        {
            return null;
        }

        GitRepositoryInfo? info;
        try
        {
            info = await _directory.FindAsync(repoId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return info is null ? null : ToRecord(repoId!, info);
    }

    private CodingRepoRecord? ToRecord(string repoId, GitRepositoryInfo info)
    {
        // The directory must answer for the id that was asked, not for a near match.
        if (!string.Equals(info.RepositoryId, repoId, StringComparison.OrdinalIgnoreCase) || info.Repo is null
            || info.Repo.Provider != "github")
        {
            return null;
        }

        var parts = (info.Repo.FullName ?? string.Empty).Split('/');
        if (parts.Length != 2 || !NamePattern().IsMatch(parts[0]) || !NamePattern().IsMatch(parts[1]))
        {
            return null;
        }

        if (!TryPositive(info.InstallationId, out var installation) || !TryPositive(info.ProviderRepositoryId, out var providerId))
        {
            return null;
        }

        var path = info.LocalClonePath;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        string clone;
        try
        {
            clone = Path.GetFullPath(path);
            if (!Directory.Exists(clone) || IsUnderRoot(clone))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException or CodingGitException)
        {
            return null;
        }

        return new CodingRepoRecord(new CodingRepoRef(repoId, _defaultBranch), clone, installation, parts[0], parts[1], providerId);
    }

    private static bool TryPositive(string? text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private bool IsUnderRoot(string clonePath)
    {
        if (_root is null)
        {
            return false;
        }

        var canonical = WorkSharePaths.Canonicalize(clonePath);
        return WorkSharePaths.IsEqualOrUnder(canonical, _root) || WorkSharePaths.IsEqualOrUnder(_root, canonical);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
