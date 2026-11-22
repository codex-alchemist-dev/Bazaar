using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Client.GitSolarSystem.Panes;

/// <summary>
/// The state the solar-system pane shows and the selection it moves. The pane view model reaches the model only through
/// this seam, so tests can substitute a fake model. The default implementation is <see cref="GitSolarStateModel"/>.
/// </summary>
internal interface IGitSolarModel
{
    /// <summary>The renderer-ready scene, or null before the first accepted snapshot.</summary>
    GitSolarScene? Scene { get; }

    /// <summary>The latest accepted layout frame for the current scene, or null when none applies.</summary>
    RenderFrame? LatestFrame { get; }

    /// <summary>True after the feed dropped, until the next snapshot or frame arrives.</summary>
    bool IsStale { get; }

    /// <summary>Applies one update. Returns false when the update was refused (an older graph version, or a frame that does not match the scene).</summary>
    bool Apply(GitSolarUpdate update);

    /// <summary>The selected commit's sha, or null when nothing is selected.</summary>
    string? SelectedSha { get; }

    /// <summary>Selects the commit with <paramref name="sha"/>. Returns false when the sha is not in the current snapshot.</summary>
    bool SelectBySha(string sha);

    /// <summary>Selects the tip commit of the branch <paramref name="name"/>. Returns false when no such branch is known.</summary>
    bool SelectBranchTip(string name);

    /// <summary>The argument for <see cref="GitSolarCommands.OpenCommit"/> for the selected commit, or null when nothing is selected.</summary>
    GitSolarOpenCommit? ToOpenCommit();
}
