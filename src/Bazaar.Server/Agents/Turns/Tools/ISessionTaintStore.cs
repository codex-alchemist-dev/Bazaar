using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Agents.Turns.Tools;

/// <summary>
/// Current taint of a session (doc 15). A session the store has never seen is <see cref="SessionTaint.Clean"/>,
/// because sessions are per task. Taint never decreases, so a store must not return a lower level than it saved.
/// It is the agent-turns home of <see cref="ISessionTaintReader"/>, which modules read through Security.Abstractions.
/// </summary>
public interface ISessionTaintStore : ISessionTaintReader
{
}
