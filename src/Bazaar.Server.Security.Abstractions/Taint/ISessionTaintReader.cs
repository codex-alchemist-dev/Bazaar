namespace Bazaar.Server.Security.Taint;

/// <summary>
/// Reads one session's taint by session id (doc 15). A session the reader has never seen is <see cref="SessionTaint.Clean"/>,
/// because sessions are per task. Taint never decreases, so a reader must not return a lower level than was saved.
/// </summary>
public interface ISessionTaintReader
{
    /// <summary>Loads the session's taint as it stands now.</summary>
    Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken);
}
