using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Commands;
using Bazaar.Client.GitGui.Panes;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.GitGui;

/// <summary>
/// The Git client module a head composes (doc 52, Client; doc 41 section 5): its pane modules, command contributions and
/// settings keys. A head passes <see cref="Modules"/> to <see cref="PaneRegistry.Compose"/> and injects what the panes read with
/// <see cref="PaneModuleContext.With{T}"/>: <see cref="IGitGuiClient"/>, <see cref="IGitProviderUiRegistry"/>,
/// <see cref="IGitRepoSource"/>, <see cref="IGitGuiSettings"/> and <see cref="Diff.DiffSurfaceRegistry"/>.
/// </summary>
public static class GitGuiPanes
{
    /// <summary>The four pane modules, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new GitViewPaneModule(),
        new GitPrsPaneModule(),
        new GitPrPaneModule(),
        new GitDiffPaneModule(),
    ];

    /// <summary>The pane kinds the modules register.</summary>
    public static IReadOnlyList<string> PaneKinds { get; } =
        [GitGuiPaneKinds.View, GitGuiPaneKinds.Prs, GitGuiPaneKinds.Pr, GitGuiPaneKinds.Diff];

    /// <summary>The commands the module contributes. Mnemonics are typed text; there are no Ctrl chords.</summary>
    public static IReadOnlyList<GitGuiCommandContribution> CommandContributions { get; } =
    [
        new(GitGuiCommandIds.ShowView, "Git: open the Git view", "git view", ["git", "repos", "branches", "commits"], null),
        new(GitGuiCommandIds.ShowPrs, "Git: list pull requests", "git prs", ["git", "pull request", "merge request", "review"], null),
        new(GitGuiCommandIds.ShowPr, "Git: open a pull request", "git pr", ["git", "pull request", "merge request"], "number"),
        new(GitGuiCommandIds.ShowDiff, "Git: open a file diff", "git diff", ["git", "diff", "patch"], "path"),
        new(GitGuiCommandIds.OpenInConstellation, "Git: open in the Constellation", "git constellation", ["git", "constellation", "branch", "commit"], "ref"),
        new(GitGuiCommandIds.OpenCommit, "Git: open a commit", "git commit", ["git", "commit", "sha"], "sha"),
    ];

    /// <summary>The palette commands for <see cref="CommandContributions"/>, for a head to load into its command registry.</summary>
    public static IReadOnlyList<Command> Commands { get; } = [.. CommandContributions.Select(c => c.ToCommand())];

    /// <summary>The settings keys the module registers, with their shipped defaults.</summary>
    public static IReadOnlyList<GitGuiSettingDefinition> SettingKeys => GitGuiSettings.Definitions;
}
