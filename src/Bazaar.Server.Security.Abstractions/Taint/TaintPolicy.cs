namespace Bazaar.Server.Security.Taint;

/// <summary>
/// The operator-set taint choices that are settings, not fixed rules (doc 15, "No (configurable)" for
/// co-members). Defaults are the most protective documented values that the design allows.
/// </summary>
/// <param name="MemberHumanTaints">
/// When true, a co-member's message taints the session softly. Default false, as in doc 15.
/// </param>
public sealed record TaintPolicy(bool MemberHumanTaints = false)
{
    /// <summary>The shipped default policy.</summary>
    public static TaintPolicy Default { get; } = new();
}
