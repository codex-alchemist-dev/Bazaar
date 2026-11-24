namespace Bazaar.Server.Playtest.Abstractions;

/// <summary>Runs a scenario headless (Godot <c>--headless</c>, Luau checks) and returns the state dump. Implementations run in <see cref="RequiredProfile"/>.</summary>
public interface IEngineHeadlessHost
{
    /// <summary>The VM profile this host runs in. The runner refuses to start unless it satisfies <see cref="HostProfile.Required"/>.</summary>
    HostProfile RequiredProfile { get; }

    Task<StateDump> RunAsync(PlaytestScenario scenario, CancellationToken cancellationToken);
}

/// <summary>Hashes the project files of a build, so a run can prove it did not edit them.</summary>
public interface IProjectHasher
{
    Task<string> HashAsync(string buildRef, CancellationToken cancellationToken);
}

/// <summary>Hands a finished run to the tool-less reader session, which writes the UX notes.</summary>
public interface IPlaytestNoteWriter
{
    Task WriteNotesAsync(PlaytestNoteRequest request, CancellationToken cancellationToken);
}

public interface IPlaytestRunStore
{
    Task SaveAsync(PlaytestResult result, CancellationToken cancellationToken);

    Task<PlaytestResult?> GetAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IPlaytestRunner
{
    Task<PlaytestResult> RunAsync(PlaytestScenario scenario, CancellationToken cancellationToken);
}
