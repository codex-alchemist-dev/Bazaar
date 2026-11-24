using System.Globalization;
using Bazaar.Server.CompanyKnowledge.Abstractions;
using Bazaar.Server.Hive;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Tools;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>
/// The names the sanitizer must keep out of company notes (docs/design/08-memory-system.md, 6.3 step 4): every project, customer and person
/// the server knows, not only an origin project. Supplied by the host; the default is empty.
/// </summary>
public interface ICompanySanitizerTerms
{
    /// <summary>The current install term list.</summary>
    HiveSanitizerTerms Current();
}

/// <summary>An <see cref="ICompanySanitizerTerms"/> with no names. Secrets are still caught.</summary>
public sealed class EmptyCompanySanitizerTerms : ICompanySanitizerTerms
{
    /// <inheritdoc />
    public HiveSanitizerTerms Current() => new(string.Empty, [], [], []);
}

/// <summary>An <see cref="ICompanyKnowledgeAudit"/> that drops every event. Replaced when the audit log (doc 48) is wired.</summary>
public sealed class NullCompanyKnowledgeAudit : ICompanyKnowledgeAudit
{
    /// <inheritdoc />
    public Task RecordAsync(string eventName, string noteId, string? actor, IReadOnlyDictionary<string, string> data, CancellationToken ct) =>
        Task.CompletedTask;
}

/// <summary>
/// The deterministic half of the company knowledge tier (docs/design/08-memory-system.md, 6.2 to 6.5): stages proposals in
/// <c>company/candidates/&lt;ulid&gt;.md</c> and admits or rejects them. It never calls a model; <see cref="ICompanyDistiller"/> is the seam
/// where that part goes. Writes go through the Vault's <see cref="IMemoryBatchSink"/>, the way Hive does for <c>hive/**</c>.
/// </summary>
public sealed class CompanyKnowledgeService
{
    private readonly IMemoryVault _vault;
    private readonly IVaultAccess _access;
    private readonly CompanyKnowledgeOptions _options;
    private readonly CompanyCandidateRegistry _registry;
    private readonly ICompanySanitizerTerms _terms;
    private readonly ICompanyAgentQuarantine? _quarantine;
    private readonly ICompanyKnowledgeAudit _audit;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public CompanyKnowledgeService(
        IMemoryVault vault,
        IVaultAccess access,
        CompanyCandidateRegistry registry,
        CompanyKnowledgeOptions? options = null,
        ICompanySanitizerTerms? terms = null,
        ICompanyAgentQuarantine? quarantine = null,
        ICompanyKnowledgeAudit? audit = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(registry);
        _vault = vault;
        _access = access;
        _registry = registry;
        _options = options ?? new CompanyKnowledgeOptions();
        _terms = terms ?? new EmptyCompanySanitizerTerms();
        _quarantine = quarantine;
        _audit = audit ?? new NullCompanyKnowledgeAudit();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Stages a proposal as <c>company/candidates/&lt;ulid&gt;.md</c>. Refuses a quarantined agent, an agent over its weekly quota, an agent claiming
    /// T3, a note that fails validation (findings are returned as codes) and a reference that does not resolve to a company note.
    /// The candidate is never retrievable and is readable only by the owner, maintenance and the proposer.
    /// </summary>
    public async Task<CompanyOutcome> StageAsync(CompanyProposal proposal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var now = _time.GetUtcNow();
        var isOwner = string.Equals(proposal.Proposer, "owner", StringComparison.Ordinal);

        if (!isOwner)
        {
            if (!proposal.Proposer.StartsWith("agent:", StringComparison.Ordinal) || proposal.Proposer.Length == "agent:".Length)
            {
                return CompanyOutcome.Refused(CompanyRefusal.Invalid, findings: [nameof(CompanyFinding.ProposerInvalid)]);
            }

            if (_quarantine?.IsQuarantined(proposal.Proposer["agent:".Length..]) != false)
            {
                // No quarantine source also refuses: an agent proposal needs a positive "not quarantined" answer (fail closed).
                return CompanyOutcome.Refused(CompanyRefusal.ProposerQuarantined);
            }

            if (_registry.CountSince(proposal.Proposer, now.AddDays(-7)) >= _options.MaxProposalsPerAgentWeek)
            {
                return CompanyOutcome.Refused(CompanyRefusal.QuotaExceeded);
            }

            if (proposal.Tier > (int)MemoryTier.AgentVerified)
            {
                return CompanyOutcome.Refused(CompanyRefusal.Invalid, findings: [nameof(CompanyFinding.TrustInvalid)]);
            }
        }

        if (proposal.Tier is < (int)MemoryTier.UntrustedDerived or > (int)MemoryTier.OwnerPromoted)
        {
            return CompanyOutcome.Refused(CompanyRefusal.Invalid, findings: [nameof(CompanyFinding.TrustInvalid)]);
        }

        var id = CompanyUlid.New(now);
        var path = "company/candidates/" + id + ".md";
        var memoryKind = CompanyNoteValidator.MemoryKindFor(proposal.Kind);
        var memory = new MemoryNote(
            id,
            MemoryNoteTypes.TypeFor(memoryKind),
            proposal.Title,
            null,
            memoryKind,
            MemoryNoteStatus.Active,
            1,
            (MemoryTier)proposal.Tier,
            isOwner ? ContentTrust.Owner : ContentTrust.Agent,
            false,
            null,
            proposal.DerivedFrom,
            [],
            null,
            proposal.Supersedes);

        var block = new CompanyBlock(
            proposal.Kind,
            proposal.Statement,
            proposal.AppliesWhen,
            proposal.NotWhen,
            proposal.Evidence,
            proposal.Proposer,
            null,
            null,
            CompanyKinds.TracksOccurrences(proposal.Kind) ? Occurrences(proposal.Evidence) : null,
            null,
            null,
            null,
            new CompanyRelations(proposal.ConditionedOn, proposal.Affects, proposal.ResolvedBy),
            null,
            null,
            proposal.DistilledByRun is { } run ? new CompanyDistilledBy(run, proposal.DistilledByReaderSession) : null);

        var note = new CompanyNote(memory, block, proposal.Body);
        var findings = CompanyNoteValidator.Validate(note, path, CompanyValidationMode.Candidate, _terms.Current());
        if (findings.Count > 0)
        {
            return CompanyOutcome.Refused(CompanyRefusal.Invalid, path, [.. findings.Select(f => f.ToString())]);
        }

        if (!await ReferencesResolveAsync(note, ct).ConfigureAwait(false))
        {
            return CompanyOutcome.Refused(CompanyRefusal.UnresolvedReference, path);
        }

        var item = new MemoryWriteItem(Guid.NewGuid(), path, note.Serialize(), null, null, "company-candidate: validated");
        var result = await _vault.CommitAsync(MemoryBatch.Create(isOwner ? _options.WriterAgentId : proposal.Proposer["agent:".Length..], [item]), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CompanyOutcome.Refused(CompanyRefusal.CommitFailed, path, detail: result.FailureReason);
        }

        _registry.Record(path, proposal.Proposer, now);
        await _audit.RecordAsync("company_knowledge.candidate.staged", id, proposal.Proposer, EvidenceIds(note), ct).ConfigureAwait(false);
        return CompanyOutcome.Ok(path);
    }

    /// <summary>
    /// Admits a candidate: validates it again for the admitted stage (the sanitizer re-runs with the current term list), checks the
    /// authority, and writes <c>company/memory/&lt;kind&gt;/&lt;slug&gt;.md</c> with the server-derived counts, confidence, review date and an
    /// <c>admitted</c> record naming the candidate, then deprecates the candidate file in the same commit. Admission never raises the tier,
    /// and a T1 candidate is never admitted.
    /// </summary>
    public async Task<CompanyOutcome> AdmitAsync(string candidatePath, CompanyAdmissionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidatePath);
        ArgumentNullException.ThrowIfNull(request);

        var read = await ReadCandidateAsync(candidatePath, ct).ConfigureAwait(false);
        if (read.Outcome is { } failed)
        {
            return failed;
        }

        var (doc, candidate) = (read.Document!, read.Note!);
        var now = _time.GetUtcNow();

        if (candidate.Memory.Trust < MemoryTier.AgentVerified)
        {
            return CompanyOutcome.Refused(CompanyRefusal.TierTooLow, candidatePath);
        }

        var by = AuthorityCheck(candidate, request, now, candidatePath, out var refusal);
        if (refusal is not null)
        {
            return refusal;
        }

        var slug = Slugify(candidate.Memory.Title);
        var targetPath = "company/memory/" + CompanyKinds.Name(candidate.Company.Kind) + "/" + slug + ".md";
        var counts = CompanyConfidence.CountsFor(candidate.Company.Evidence);
        var interval = candidate.Memory.Trust == MemoryTier.OwnerPromoted ? _options.ReviewIntervalDaysT3 : _options.ReviewIntervalDaysT2;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var reviewBy = today.AddDays(interval);
        var stamp = now.ToString("O", CultureInfo.InvariantCulture);

        var admittedBlock = candidate.Company with
        {
            Counts = counts,
            Confidence = CompanyConfidence.Derive(counts, reviewBy, null, today, _options.ConfirmedMinProjects, linkedContradiction: false),
            ReviewBy = reviewBy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Admitted = new CompanyAdmitted(by, stamp, Path.GetFileNameWithoutExtension(candidatePath)),
            Promoted = candidate.Memory.Trust == MemoryTier.OwnerPromoted ? new CompanyPromoted("T3", "owner", stamp, null) : null,
        };
        var admitted = new CompanyNote(candidate.Memory with { Version = 1 }, admittedBlock, candidate.Body);

        var findings = CompanyNoteValidator.Validate(admitted, targetPath, CompanyValidationMode.Admitted, _terms.Current());
        if (findings.Count > 0)
        {
            return CompanyOutcome.Refused(CompanyRefusal.Invalid, candidatePath, [.. findings.Select(f => f.ToString())]);
        }

        if (!await ReferencesResolveAsync(admitted, ct).ConfigureAwait(false))
        {
            return CompanyOutcome.Refused(CompanyRefusal.UnresolvedReference, candidatePath);
        }

        if (VaultPath.TryParse(targetPath, out var target) && await _vault.ReadNoteAsync(target, ct).ConfigureAwait(false) is not null)
        {
            return CompanyOutcome.Refused(CompanyRefusal.Duplicate, candidatePath);
        }

        var items = new[]
        {
            new MemoryWriteItem(Guid.NewGuid(), targetPath, admitted.Serialize(), null, null, "company-admit: validated"),
            Deprecated(doc, candidatePath, "admitted as " + targetPath, by, stamp),
        };
        var result = await _vault.CommitAsync(MemoryBatch.Create(_options.WriterAgentId, items), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CompanyOutcome.Refused(CompanyRefusal.CommitFailed, candidatePath, detail: result.FailureReason);
        }

        _registry.Remove(candidatePath);
        var data = new Dictionary<string, string>(EvidenceIds(admitted)) { ["admitted_by"] = by, ["path"] = targetPath };
        await _audit.RecordAsync("company_knowledge.admitted", admitted.Memory.Id, candidate.Company.ProposedBy, data, ct).ConfigureAwait(false);
        return CompanyOutcome.Ok(targetPath);
    }

    /// <summary>Rejects a candidate: the file stays, set to deprecated with the reason (no hard delete, doc 08 section 2).</summary>
    public async Task<CompanyOutcome> RejectAsync(string candidatePath, string rejectedBy, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedBy);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var read = await ReadCandidateAsync(candidatePath, ct, validate: false).ConfigureAwait(false);
        if (read.Outcome is { } failed)
        {
            return failed;
        }

        var item = Deprecated(read.Document!, candidatePath, "rejected: " + reason, rejectedBy, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        var result = await _vault.CommitAsync(MemoryBatch.Create(_options.WriterAgentId, [item]), ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return CompanyOutcome.Refused(CompanyRefusal.CommitFailed, candidatePath, detail: result.FailureReason);
        }

        _registry.Remove(candidatePath);
        await _audit.RecordAsync("company_knowledge.rejected", read.Note!.Memory.Id, rejectedBy, new Dictionary<string, string>(), ct).ConfigureAwait(false);
        return CompanyOutcome.Ok(candidatePath);
    }

    private string AuthorityCheck(CompanyNote candidate, CompanyAdmissionRequest request, DateTimeOffset now, string path, out CompanyOutcome? refusal)
    {
        refusal = null;
        var required = candidate.Memory.Trust == MemoryTier.OwnerPromoted ? CompanyAdmissionAuthority.Owner : _options.MinAuthority;
        if (request.Authority < required)
        {
            refusal = CompanyOutcome.Refused(CompanyRefusal.AuthorityInsufficient, path);
            return string.Empty;
        }

        switch (request.Authority)
        {
            case CompanyAdmissionAuthority.Owner:
                return "owner";

            case CompanyAdmissionAuthority.Council:
                if (string.IsNullOrWhiteSpace(request.ApprovedBy))
                {
                    refusal = CompanyOutcome.Refused(CompanyRefusal.AuthorityInsufficient, path);
                    return string.Empty;
                }

                return "council:" + request.ApprovedBy;

            default:
                // review_days = 0 means the owner must admit every candidate; an open objection also blocks the window.
                var staged = CompanyUlid.TimeOf(candidate.Memory.Id);
                if (_options.ReviewDays <= 0 || request.Objected || staged is null || now < staged.Value.AddDays(_options.ReviewDays))
                {
                    refusal = CompanyOutcome.Refused(CompanyRefusal.ReviewWindowOpen, path);
                    return string.Empty;
                }

                return "review-window";
        }
    }

    private sealed record CandidateRead(NoteDocument? Document, CompanyNote? Note, CompanyOutcome? Outcome);

    private async Task<CandidateRead> ReadCandidateAsync(string path, CancellationToken ct, bool validate = true)
    {
        CandidateRead Fail(CompanyRefusal refusal, IReadOnlyList<string>? findings = null) =>
            new(null, null, CompanyOutcome.Refused(refusal, path, findings));

        if (!path.StartsWith("company/candidates/", StringComparison.Ordinal) || !VaultPath.TryParse(path, out var vaultPath)
            || !_access.CanRead(VaultPrincipal.Maintenance(), vaultPath))
        {
            return Fail(CompanyRefusal.NotFound);
        }

        var text = await _vault.ReadNoteAsync(vaultPath, ct).ConfigureAwait(false);
        if (text is null)
        {
            return Fail(CompanyRefusal.NotFound);
        }

        var parsed = CompanyNote.Parse(text);
        if (parsed.Note is not { } note || NoteDocument.Parse(text).Document is not { } doc)
        {
            return Fail(CompanyRefusal.Invalid, [.. parsed.Findings.Select(f => f.ToString())]);
        }

        if (note.Memory.Status != MemoryNoteStatus.Active)
        {
            return Fail(CompanyRefusal.NotACandidate);
        }

        if (validate)
        {
            var findings = CompanyNoteValidator.Validate(note, path, CompanyValidationMode.Candidate, _terms.Current());
            if (findings.Count > 0)
            {
                return Fail(CompanyRefusal.Invalid, [.. findings.Select(f => f.ToString())]);
            }
        }

        return new CandidateRead(doc, note, null);
    }

    private static MemoryWriteItem Deprecated(NoteDocument doc, string path, string reason, string by, string at)
    {
        var next = doc.Note with
        {
            Status = MemoryNoteStatus.Deprecated,
            Version = doc.Note.Version + 1,
            Deprecated = new MemoryDeprecation(reason, by, at),
        };
        return new MemoryWriteItem(Guid.NewGuid(), path, doc.WithNote(next).Serialize(), null, null, "company-candidate: closed");
    }

    /// <summary>Rule 2: every referenced id must resolve, through the Vault, to a note under <c>company/</c>. Unresolvable means refused.</summary>
    private async Task<bool> ReferencesResolveAsync(CompanyNote note, CancellationToken ct)
    {
        var targets = note.Memory.DerivedFrom.Concat(note.Memory.Supersedes)
            .Concat(note.Company.Relations.ConditionedOn).Concat(note.Company.Relations.Affects).Concat(note.Company.Relations.ResolvedBy)
            .Distinct(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            VaultPath? resolved;
            if (target.StartsWith("company/", StringComparison.Ordinal))
            {
                resolved = VaultPath.TryParse(target, out var p) && await _vault.ReadNoteAsync(p, ct).ConfigureAwait(false) is not null ? p : null;
            }
            else
            {
                resolved = await _vault.FindNoteByIdAsync(target, ct).ConfigureAwait(false);
            }

            if (resolved is null || !resolved.Value.StartsWith("company/", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static CompanyOccurrences? Occurrences(IReadOnlyList<CompanyEvidence> evidence)
    {
        var dates = evidence.Where(e => e.At is not null).Select(e => e.At![..Math.Min(10, e.At!.Length)]).Order(StringComparer.Ordinal).ToList();
        var sources = evidence.Where(e => e.DocId is not null).Select(e => e.DocId).Distinct(StringComparer.Ordinal).Count();
        return dates.Count == 0 ? new CompanyOccurrences(sources, string.Empty, string.Empty) : new CompanyOccurrences(sources, dates[0], dates[^1]);
    }

    private static Dictionary<string, string> EvidenceIds(CompanyNote note) => new()
    {
        ["sources"] = string.Join(',', note.Company.Evidence.Where(e => e.DocId is not null).Select(e => e.DocId)),
        ["run"] = note.Company.DistilledBy?.Run ?? string.Empty,
    };

    private static string Slugify(string title)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > 64)
        {
            slug = slug[..64].TrimEnd('-');
        }

        return slug.Length == 0 ? "note" : slug;
    }
}
