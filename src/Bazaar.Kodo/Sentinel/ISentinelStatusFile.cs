namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// The loopback status seam (doc 46 sections 6.3 and 7.8). The sentinel replaces the whole status file on every change, so a
/// reader never sees a partial record. The implementation replaces the file atomically, as the pointer does.
/// </summary>
public interface ISentinelStatusFile
{
    /// <summary>Replaces the status file with <paramref name="bytes"/>.</summary>
    void Replace(ReadOnlyMemory<byte> bytes);
}
