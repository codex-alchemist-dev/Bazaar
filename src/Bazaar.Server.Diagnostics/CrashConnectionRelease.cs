namespace Bazaar.Server.Diagnostics;

/// <summary>Drops a closed connection's half-finished screenshot uploads. Held reports and finished uploads are keyed by device and expire on their own.</summary>
public sealed class CrashConnectionRelease(AttachmentUploadAssembler uploads) : ICrashConnectionRelease
{
    /// <inheritdoc />
    public void Release(Guid connectionId) => uploads.Forget(connectionId);
}
