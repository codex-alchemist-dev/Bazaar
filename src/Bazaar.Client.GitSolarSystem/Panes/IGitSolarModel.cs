using Bazaar.Client.GitSolarSystem.Abstractions;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>
/// The state the solar-system pane shows and the selection it moves. The pane view model reaches the model only through
/// this seam, so a scene-building model can replace <see cref="SimpleGitSolarModel"/> without changing the pane.
/// </summary>
internal interface IGitSolarModel
{
    /// <summary>The renderer-ready scene, or null when the model builds none.</summary>
    GitSolarScene? Scene { get; }

    /// <summary>True after the feed dropped, until the next snapshot arrives.</summary>
    bool IsStale { get; }

    /// <summary>Applies one update. Returns false when the update was refused (an older graph version, or a frame this model does not use).</summary>
    bool Apply(GitSolarUpdate update);

    /// <summary>The selected commit's sha, or null when nothing is selected.</summary>
    string? SelectedSha { get; }

    /// <summary>Selects the commit with <paramref name="sha"/>. Returns false when the sha is not in the current snapshot.</summary>
    bool SelectBySha(string sha);

    /// <summary>Selects the tip commit of the branch <paramref name="name"/>. Returns false when no such branch is known.</summary>
    bool SelectBranchTip(string name);

    /// <summary>Moves the selection to the next row in time order. Selects the first row when nothing is selected.</summary>
    void MoveNext();

    /// <summary>Moves the selection to the previous row in time order. Selects the first row when nothing is selected.</summary>
    void MovePrevious();

    /// <summary>The argument for <see cref="GitSolarCommands.OpenCommit"/> for the selected commit, or null when nothing is selected.</summary>
    GitSolarOpenCommit? ToOpenCommit();
}
