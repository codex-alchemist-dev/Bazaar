using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// Current taint of a session (doc 15). A session the store has never seen is <see cref="SessionTaint.Clean"/>,
/// because sessions are per task. Taint never decreases, so a store must not return a lower level than it saved.
/// </summary>
public interface ISessionTaintStore
{
    /// <summary>Loads the session's taint as it stands now.</summary>
    Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken);
}
