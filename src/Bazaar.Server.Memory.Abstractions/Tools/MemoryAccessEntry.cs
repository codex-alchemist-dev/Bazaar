namespace Bazaar.Server.Memory.Tools;

/// <summary>One logged read of another agent's private memory by the owner (docs/design/08-memory-system.md, section 8).</summary>
/// <param name="Reader">Who read: the owner's label.</param>
/// <param name="Path">The private note path that was read.</param>
/// <param name="Reason">The access decision reason.</param>
/// <param name="At">When the read happened.</param>
public sealed record MemoryAccessEntry(string Reader, string Path, string Reason, DateTimeOffset At);
