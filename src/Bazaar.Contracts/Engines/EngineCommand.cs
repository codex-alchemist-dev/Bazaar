using System.Text.Json;

namespace Bazaar.Contracts.Engines;

/// <summary>
/// One command sent to an open engine session (the <c>command</c> method, docs/design/20-engine-integration-api.md).
/// <c>CommandId</c> makes retries safe: the adapter may answer a repeated id from its record instead of running it again.
/// </summary>
public sealed record EngineCommand(
    string CommandId,
    string Name,
    JsonElement? Arguments = null);

/// <summary>
/// The outcome of an <see cref="EngineCommand"/>. A non-zero <c>ExitCode</c> is a failure: the adapter never assumes
/// success from silence, because Godot ignores unknown flags, so the exit code and parsed error lines are both checked.
/// </summary>
public sealed record CommandResult(
    string CommandId,
    int ExitCode,
    string Output,
    IReadOnlyList<string> Errors);
