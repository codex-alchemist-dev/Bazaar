using Bazaar.Contracts.Graph;

namespace Bazaar.Client.GitSolarSystem.Abstractions;

/// <summary>Command ids the solar-system view shares with other modules (doc 19, doc 52). Commands carry data only, so no module references another.</summary>
public static class GitSolarCommands
{
    /// <summary>Emitted by the solar-system view for the selected commit. Argument: <see cref="GitSolarOpenCommit"/>.</summary>
    public const string OpenCommit = "git.open-commit";

    /// <summary>Handled by the solar-system view: select and centre a commit. Argument: the sha.</summary>
    public const string FocusCommit = "git.focus-commit";

    /// <summary>Handled by the solar-system view: select the tip of a branch. Argument: the branch name.</summary>
    public const string FocusBranch = "git.focus-branch";
}

/// <summary>The argument of <see cref="GitSolarCommands.OpenCommit"/>.</summary>
public sealed record GitSolarOpenCommit(string Sha, string Branch);

/// <summary>One update from the server: a snapshot, or a layout frame for the snapshot of the same graph version.</summary>
public abstract record GitSolarUpdate
{
    private GitSolarUpdate() { }

    public sealed record Snapshot(GitSolarSnapshot Value) : GitSolarUpdate;

    public sealed record Frame(LayoutFrame Value) : GitSolarUpdate;

    /// <summary>The connection dropped. The view keeps its last state and marks it stale.</summary>
    public sealed record Disconnected : GitSolarUpdate;
}

/// <summary>The seam to the server's solar-system feed. A head injects it with <c>PaneModuleContext.With</c>; tests use a fake.</summary>
public interface IGitSolarSource
{
    /// <summary>Updates for one repository, starting with a snapshot, then frames. Ends when <paramref name="cancellationToken"/> is cancelled.</summary>
    IAsyncEnumerable<GitSolarUpdate> UpdatesAsync(string repository, CancellationToken cancellationToken);
}
