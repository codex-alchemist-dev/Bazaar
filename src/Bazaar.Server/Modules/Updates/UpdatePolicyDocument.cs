namespace Bazaar.Server.Modules.Updates;

/// <summary>
/// An owner update policy document: the policies as code (doc 49 section 7, "Policy-as-code"). The file is versioned in the
/// company's config repository and validated on load by <see cref="UpdatePolicyDocumentLoader"/>.
/// </summary>
/// <remarks>
/// <para>JSON shape. Property names are camelCase. Unknown properties are refused, not ignored.</para>
/// <code>
/// {
///   "schemaVersion": 1,
///   "policies": [
///     {
///       "scope": { "moduleId": "postgresql", "category": "security", "versionType": "generalAvailability", "channel": "stable" },
///       "conditions": { "isSecurity": true, "matrixPassed": true },
///       "windows": [
///         { "days": ["sunday"], "start": "02:00", "end": "04:00", "zone": { "timeZone": "Europe/Berlin" } }
///       ],
///       "action": "auto"
///     },
///     { "action": "manual" }
///   ]
/// }
/// </code>
/// <list type="bullet">
/// <item><c>schemaVersion</c>: required, must be <see cref="SchemaVersion"/> (1).</item>
/// <item><c>policies</c>: required, ordered. The first listed policy wins a tie in specificity (see <see cref="UpdatePolicyEvaluator"/>).</item>
/// <item><c>scope</c>: optional; each of <c>moduleId</c>, <c>category</c>, <c>channel</c> is a non-empty string, and
/// <c>versionType</c> is <c>generalAvailability</c>, <c>releaseCandidate</c> or <c>beta</c>. A missing field matches anything.</item>
/// <item><c>conditions</c>: optional; <c>isSecurity</c> and <c>matrixPassed</c> are booleans. A missing field matches either value.</item>
/// <item><c>windows</c>: optional; an empty or missing list means the module's default windows. Each window has <c>days</c>
/// (lowercase day names, for example <c>"monday"</c>), <c>start</c> and <c>end</c> as <c>HH:mm</c> (end exclusive, no midnight
/// crossing), and a <c>zone</c> with exactly one of <c>timeZone</c> (an IANA or Windows zone id) or <c>utcOffset</c>
/// (<c>+hh:mm</c> or <c>-hh:mm</c>).</item>
/// <item><c>action</c>: required; one of <c>auto</c>, <c>manual</c>, <c>defer</c>, <c>pin</c>, <c>skip</c>, <c>requireApproval</c>.</item>
/// </list>
/// <para>
/// Invariant: an <c>auto</c> policy whose scope is pinned to a pre-release version type (<c>beta</c> or
/// <c>releaseCandidate</c>) is refused at load time. Beta and release candidates are never automatic (doc 42 section 10.1).
/// </para>
/// </remarks>
public sealed record UpdatePolicyDocument(IReadOnlyList<UpdatePolicy> Policies)
{
    /// <summary>The only schema version this build reads and writes.</summary>
    public const int SchemaVersion = 1;
}
