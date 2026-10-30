using System.Text;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Memory.Notes;

/// <summary>Reason codes from <see cref="SoulGuard"/>. Flag-only codes do not reject the write.</summary>
public enum SoulGuardReason
{
    /// <summary>Front matter does not parse. Rejects.</summary>
    FrontMatterInvalid,

    /// <summary>The note is larger than the configured limit. Rejects.</summary>
    TooLarge,

    /// <summary>Neither an origin nor derived_from is present. Rejects.</summary>
    MissingProvenance,

    /// <summary>The note type does not match its kind. Rejects.</summary>
    TypeKindMismatch,

    /// <summary>Status and the deprecation record disagree. Rejects.</summary>
    DeprecationInconsistent,

    /// <summary>The path is outside the memory areas, or not valid for the kind. Rejects.</summary>
    AreaInvalid,

    /// <summary>The acting principal may not write this kind to this area. Rejects.</summary>
    ActorNotAllowed,

    /// <summary>A front matter key would set identity, personality, model policy, grants or Passport references. Rejects.</summary>
    SoulCoreKey,

    /// <summary>A credential-shaped token was found. Rejects.</summary>
    SecretDetected,

    /// <summary>The note claims a more trusted provenance than the server recorded. Rejects.</summary>
    ClaimedTrustExceedsProvenance,

    /// <summary>The write comes from a quarantined source and is retrieval-eligible. Rejects.</summary>
    QuarantinedSource,

    /// <summary>The write comes from a quarantined source and is kept as a non-retrieved record at T0. Flag only.</summary>
    QuarantinedRecordOnly,

    /// <summary>The text claims a role, permission or approval. Flags the write.</summary>
    AuthorityClaim,

    /// <summary>The tier was forced to T1 because of an authority claim. Flag only.</summary>
    TierForcedUntrusted,

    /// <summary>The note's claimed tier differs from the tier the server assigned. Flag only.</summary>
    ClaimedTierOverridden,
}

/// <summary>What a write asks for: where it goes, who makes it and how it is retrieved.</summary>
/// <param name="TargetPath">Vault-relative path the note will be written to.</param>
/// <param name="Provenance">Server-known trust of the write (source, session taint, lineage and actor).</param>
/// <param name="ActorAgent">The agent slug when the actor is an agent; otherwise null.</param>
/// <param name="RetrievalEligible">False for quarantined writes kept only as evidence, never retrieved.</param>
public sealed record MemoryWriteRequest(
    string TargetPath,
    MemoryWriteInput Provenance,
    string? ActorAgent,
    bool RetrievalEligible = true);

/// <summary>
/// The outcome of SoulGuard. <see cref="Allowed"/> is false when any rejecting reason is present.
/// <see cref="Tier"/> is the tier the server assigns, whatever the note claims.
/// </summary>
/// <param name="Allowed">True when the write may proceed.</param>
/// <param name="Tier">The assigned promotion tier.</param>
/// <param name="Flagged">True when the text claimed a role, permission or approval.</param>
/// <param name="Reasons">Every reason found, rejecting and flag-only.</param>
/// <param name="Detail">A structural note on a parse failure, or null.</param>
public sealed record SoulGuardResult(
    bool Allowed,
    MemoryTier Tier,
    bool Flagged,
    IReadOnlyList<SoulGuardReason> Reasons,
    string? Detail);

/// <summary>
/// Structural and safety checks on a memory write before the writer sees it (docs/design/08-memory-system.md,
/// section 2). Covers steps 1 to 3: structure (kind and area for the actor, size, provenance, front matter),
/// Soul core reach (with authority claims stored at T1 and flagged) and the secret scan. Pure: no I/O, and
/// it never throws on user content.
/// <para>
/// Seam for the later steps, owned by the memory store and not here: step 4 (duplicates by embedding
/// similarity, which turns a store into an update) and step 5 (stale <c>expectedVersion</c> and project
/// contradictions) run after this check; step 6 (personal-data span encryption) runs before the write.
/// </para>
/// </summary>
public static class SoulGuard
{
    /// <summary>The shipped default of <c>memory.note_max_bytes</c>. Callers pass the configured value.</summary>
    public const int DefaultMaxNoteBytes = 4096;

    private static readonly MemoryKind[] _agentKinds = [MemoryKind.Procedural, MemoryKind.Failure, MemoryKind.Preference, MemoryKind.Working];

    private static readonly HashSet<string> _soulCoreKeys = new(StringComparer.Ordinal)
    {
        "identity",
        "personality",
        "soul",
        "soul_core",
        "model",
        "models",
        "model_whitelist",
        "model_policy",
        "whitelist",
        "grant",
        "grants",
    };

    /// <summary>
    /// Validates a note's text for a write.
    /// </summary>
    /// <param name="noteText">The full note text: front matter and body.</param>
    /// <param name="request">The target path, the server-known provenance and the actor.</param>
    /// <param name="maxNoteBytes">The configured size limit, UTF-8 bytes including front matter.</param>
    public static SoulGuardResult Validate(string noteText, MemoryWriteRequest request, int maxNoteBytes)
    {
        ArgumentNullException.ThrowIfNull(noteText);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNoteBytes, 1);

        var provenance = request.Provenance;
        var tier = MemoryTrustRules.TierForWrite(provenance);
        var reasons = new List<SoulGuardReason>();

        if (Encoding.UTF8.GetByteCount(noteText) > maxNoteBytes)
        {
            reasons.Add(SoulGuardReason.TooLarge);
        }

        // Scanned on the raw text, so a secret is caught even when the front matter does not parse.
        if (SecretScanner.ContainsSecret(noteText))
        {
            reasons.Add(SoulGuardReason.SecretDetected);
        }

        var parsed = NoteDocument.Parse(noteText);
        if (!parsed.IsSuccess)
        {
            reasons.Add(SoulGuardReason.FrontMatterInvalid);
            return Result(reasons, tier, parsed.Detail);
        }

        var document = parsed.Document!;
        var note = document.Note;

        var area = MemoryAreas.CheckPath(request.TargetPath, note.Kind);
        if (!area.IsValid)
        {
            reasons.Add(SoulGuardReason.AreaInvalid);
        }

        if (!string.Equals(note.Type, MemoryNoteTypes.TypeFor(note.Kind), StringComparison.Ordinal))
        {
            reasons.Add(SoulGuardReason.TypeKindMismatch);
        }

        if ((note.Status == MemoryNoteStatus.Deprecated) != (note.Deprecated is not null))
        {
            reasons.Add(SoulGuardReason.DeprecationInconsistent);
        }

        if (note.Origin is null && note.DerivedFrom.Count == 0)
        {
            reasons.Add(SoulGuardReason.MissingProvenance);
        }

        if (area.IsValid && !MayWrite(request, note.Kind, area))
        {
            reasons.Add(SoulGuardReason.ActorNotAllowed);
        }

        if (Rank(note.SourceTrust) < Rank(provenance.SourceTrust))
        {
            reasons.Add(SoulGuardReason.ClaimedTrustExceedsProvenance);
        }

        if (provenance.SourceTrust == ContentTrust.Quarantined)
        {
            reasons.Add(request.RetrievalEligible ? SoulGuardReason.QuarantinedSource : SoulGuardReason.QuarantinedRecordOnly);
        }

        if (TouchesSoulCore(document.ExtraFields))
        {
            reasons.Add(SoulGuardReason.SoulCoreKey);
        }

        // Authority claims in the title or body are stored, not rejected: they drop to T1 and are flagged.
        if (AuthorityClaimScanner.ClaimsAuthority(note.Title + "\n" + document.Body))
        {
            reasons.Add(SoulGuardReason.AuthorityClaim);
            if (tier > MemoryTier.UntrustedDerived)
            {
                tier = MemoryTier.UntrustedDerived;
                reasons.Add(SoulGuardReason.TierForcedUntrusted);
            }
        }

        if (note.Trust != tier)
        {
            reasons.Add(SoulGuardReason.ClaimedTierOverridden);
        }

        return Result(reasons, tier, null);
    }

    private static SoulGuardResult Result(List<SoulGuardReason> reasons, MemoryTier tier, string? detail) =>
        new(
            Allowed: !reasons.Any(IsRejecting),
            Tier: tier,
            Flagged: reasons.Contains(SoulGuardReason.AuthorityClaim),
            Reasons: reasons,
            Detail: detail);

    private static bool IsRejecting(SoulGuardReason reason) => reason is not (
        SoulGuardReason.QuarantinedRecordOnly
        or SoulGuardReason.AuthorityClaim
        or SoulGuardReason.TierForcedUntrusted
        or SoulGuardReason.ClaimedTierOverridden);

    /// <summary>
    /// Who may write what. Owner and system may write anything. An agent may write its four kinds to its
    /// own private area, or to project and company areas. No one else writes memory notes, and hive copies
    /// come only from the copy flow (system).
    /// </summary>
    private static bool MayWrite(MemoryWriteRequest request, MemoryKind kind, MemoryAreaCheck area)
    {
        switch (request.Provenance.Actor)
        {
            case MemoryActor.Owner:
            case MemoryActor.System:
                return true;
            case MemoryActor.Agent:
                return request.ActorAgent is not null
                    && area.Scope != MemoryScope.Hive
                    && Array.IndexOf(_agentKinds, kind) >= 0
                    && (area.Scope != MemoryScope.Private || string.Equals(area.Owner, request.ActorAgent, StringComparison.Ordinal));
            default:
                return false;
        }
    }

    /// <summary>
    /// Front matter may not set Soul core fields. Keys are compared case-insensitively with '-' read as '_',
    /// at any depth, and any key containing "passport" counts as a Passport reference.
    /// </summary>
    private static bool TouchesSoulCore(IEnumerable<KeyValuePair<string, FrontValue>> entries) =>
        entries.Any(e => IsSoulCoreKey(e.Key) || NestedTouchesSoulCore(e.Value));

    private static bool NestedTouchesSoulCore(FrontValue value) => value switch
    {
        FrontMapping mapping => TouchesSoulCore(mapping.Entries),
        FrontSequence sequence => sequence.Items.Any(NestedTouchesSoulCore),
        _ => false,
    };

    private static bool IsSoulCoreKey(string key)
    {
        var normalized = key.ToLowerInvariant().Replace('-', '_');
        return _soulCoreKeys.Contains(normalized) || normalized.Contains("passport", StringComparison.Ordinal);
    }

    /// <summary>
    /// Ordering of provenance, most trusted first: owner, system, member-human, agent, external, quarantined.
    /// A note may claim its recorded provenance or a less trusted one, never a more trusted one.
    /// </summary>
    private static int Rank(ContentTrust trust) => trust switch
    {
        ContentTrust.Owner => 0,
        ContentTrust.System => 1,
        ContentTrust.MemberHuman => 2,
        ContentTrust.Agent => 3,
        ContentTrust.External => 4,
        ContentTrust.Quarantined => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(trust), trust, "Unknown source trust."),
    };
}
