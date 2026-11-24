namespace Bazaar.Server.Memory.Tools;

/// <summary>A refused memory tool call.</summary>
/// <param name="Code">The refusal kind.</param>
/// <param name="Message">A short reason. Contains reason codes and field names only, never note text.</param>
public sealed record MemoryToolError(MemoryToolErrorCode Code, string Message);
