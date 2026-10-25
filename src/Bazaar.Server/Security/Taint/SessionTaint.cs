using System.Collections.Immutable;

namespace Bazaar.Server.Security.Taint;

/// <summary>
/// A session's taint: its level and every source that raised it. Immutable; <see cref="TaintRules.Apply"/>
/// returns a new value. Sessions are per task, so a new session starts Clean.
/// </summary>
/// <param name="State">Current level. Never decreases.</param>
/// <param name="Sources">Each content item that raised the level, in arrival order.</param>
public sealed record SessionTaint(TaintState State, ImmutableArray<TaintSource> Sources)
{
    /// <summary>A session with no untrusted input.</summary>
    public static SessionTaint Clean { get; } = new(TaintState.Clean, ImmutableArray<TaintSource>.Empty);
}
