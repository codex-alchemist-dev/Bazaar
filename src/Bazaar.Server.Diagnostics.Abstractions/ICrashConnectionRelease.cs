namespace Bazaar.Server.Diagnostics;

/// <summary>
/// Releases what the crash module holds for one wire connection when it ends. The host bridges the wire layer's connection-close
/// hook to this seam, so the module never references the wire contracts (doc 43 section 5.2).
/// </summary>
public interface ICrashConnectionRelease
{
    /// <summary>Drops the open screenshot uploads of the connection and gives their memory back. Safe to call twice.</summary>
    void Release(Guid connectionId);
}
