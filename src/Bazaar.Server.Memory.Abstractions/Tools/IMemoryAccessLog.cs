namespace Bazaar.Server.Memory.Tools;

/// <summary>Records owner reads of another agent's private memory. Every such read is logged before its result is returned.</summary>
public interface IMemoryAccessLog
{
    /// <summary>Appends one entry.</summary>
    void Record(MemoryAccessEntry entry);
}
