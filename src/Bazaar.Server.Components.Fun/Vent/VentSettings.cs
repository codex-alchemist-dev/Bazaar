namespace Bazaar.Server.Fun.Vent;

/// <summary>
/// The vent settings from doc 26 (<c>vent.*</c>) with the shipped defaults. The publish target is an operator-set value
/// from doc 36; the internal General Bazaar GC is the default.
/// </summary>
public sealed class VentSettings
{
    /// <summary><c>vent.required_before_card</c>: a draft is written before an agent claims a new card after a failure.</summary>
    public bool RequiredBeforeCard { get; init; } = true;

    /// <summary><c>vent.publish_target</c>: where a published vent goes.</summary>
    public string PublishTarget { get; init; } = InternalGeneralGc;

    /// <summary>The internal default target.</summary>
    public const string InternalGeneralGc = "internal_general_gc";

    /// <summary>
    /// The external targets the operator has configured (doc 36). Empty by default; an external target also needs
    /// counsel review before it is listed here.
    /// </summary>
    public IReadOnlySet<string> OperatorTargets { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
