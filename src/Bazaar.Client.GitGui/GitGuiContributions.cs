using Bazaar.Client.Core.Commands;

namespace Bazaar.Client.GitGui;

/// <summary>The pane kinds this module registers (doc 52, Client).</summary>
public static class GitGuiPaneKinds
{
    /// <summary>Repos, branches and recent commits.</summary>
    public const string View = "git.view";

    /// <summary>The PR list of one repo. Model: <c>{ "provider", "repo" }</c>.</summary>
    public const string Prs = "git.prs";

    /// <summary>One PR. Model: <c>{ "provider", "repo", "number" }</c>.</summary>
    public const string Pr = "git.pr";

    /// <summary>One PR file's diff. Model: <c>{ "provider", "repo", "number", "path" }</c>; without a path the first file shows.</summary>
    public const string Diff = "git.diff";
}

/// <summary>The command ids this module contributes. The Constellation link goes through these, never through a reference (doc 52).</summary>
public static class GitGuiCommandIds
{
    public const string ShowView = "git.show-view";
    public const string ShowPrs = "git.show-prs";
    public const string ShowPr = "git.show-pr";
    public const string ShowDiff = "git.show-diff";

    /// <summary>Parameter: a branch name, ref or commit sha. The Constellation module handles it.</summary>
    public const string OpenInConstellation = "git.open-in-constellation";

    /// <summary>Parameter: a commit sha. The Git view handles it.</summary>
    public const string OpenCommit = "git.open-commit";
}

/// <summary>
/// One command the module contributes: id, title, mnemonic and keywords (doc 41 section 5), plus the name of the parameter the
/// command takes, when it takes one. Mnemonics are typed text; no Ctrl chords.
/// </summary>
public sealed record GitGuiCommandContribution(string Id, string Title, string Mnemonic, IReadOnlyList<string> Keywords, string? ParameterName)
{
    /// <summary>The palette command for this contribution.</summary>
    public Command ToCommand() => new(Id, Title, Mnemonic, Keywords, CommandSource.Local);
}
