using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Chaos;

/// <summary>
/// The auditable record of a gate decision (doc 46 section 17.7). Identifiers, codes, times and a digest only. A value that is not an
/// identifier is stored as <c>redacted</c>. The digest is the SHA-256 of a fixed text form, so the same decision always gives the same digest;
/// writing the record to the journal or the audit log (doc 18) is the host's job.
/// </summary>
/// <param name="Version">The record version, 1.</param>
/// <param name="DecidedAt">When the gate decided.</param>
/// <param name="ActorId">Who asked.</param>
/// <param name="PlanId">The plan.</param>
/// <param name="TargetId">The target.</param>
/// <param name="TargetClass">How the target was classified.</param>
/// <param name="Allowed">The outcome.</param>
/// <param name="Codes">The decision codes, in order.</param>
/// <param name="Digest">Lower-case hex SHA-256 of the fields above.</param>
public sealed record ChaosAuditRecord(
    int Version,
    DateTimeOffset DecidedAt,
    string ActorId,
    string PlanId,
    string TargetId,
    ChaosTargetClass TargetClass,
    bool Allowed,
    IReadOnlyList<string> Codes,
    string Digest)
{
    /// <summary>The value stored for an identifier that failed the shape check.</summary>
    public const string Redacted = "redacted";

    /// <summary>Builds the record for a decision.</summary>
    public static ChaosAuditRecord Create(ChaosSettings settings, ChaosRequest? request, ChaosDecision decision, DateTimeOffset decidedAt)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(decision);
        string actor = Safe(request?.ActorId);
        string plan = Safe(request?.Plan?.Id);
        string target = Safe(request?.Plan?.TargetId);
        ChaosTargetClass targetClass = ChaosTargetClassifier.Classify(request?.Plan?.TargetId, settings.KnownProductionIds);
        string[] codes = [.. decision.Codes];
        string text = string.Join(
            '\n',
            "chaos.audit.v1",
            decidedAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            actor,
            plan,
            target,
            targetClass.ToString(),
            decision.Allowed ? "allowed" : "refused",
            string.Join(',', codes));
        string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return new ChaosAuditRecord(1, decidedAt, actor, plan, target, targetClass, decision.Allowed, codes, digest);
    }

    private static string Safe(string? value) =>
        !string.IsNullOrEmpty(value) && KodoExportPatterns.IdentifierValue().IsMatch(value) ? value : Redacted;
}
