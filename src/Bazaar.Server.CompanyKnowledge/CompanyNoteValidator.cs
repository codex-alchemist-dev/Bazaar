using System.Text.RegularExpressions;
using Bazaar.Server.CompanyKnowledge.Abstractions;
using Bazaar.Server.Hive;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;

namespace Bazaar.Server.CompanyKnowledge;

/// <summary>Validation failures for a company note. Codes only: a finding never quotes note text.</summary>
public enum CompanyFinding
{
    /// <summary>The note's front matter does not parse.</summary>
    NotParseable,

    /// <summary>A top-level front matter key other than <c>company</c> beyond the memory schema.</summary>
    UnknownTopLevelField,

    /// <summary>No <c>company</c> block.</summary>
    MissingCompanyBlock,

    /// <summary>A key in the <c>company</c> block the schema does not define.</summary>
    UnknownCompanyField,

    /// <summary><c>company.kind</c> is missing or not one of the six kinds.</summary>
    KindInvalid,

    /// <summary>An evidence entry is malformed.</summary>
    EvidenceMalformed,

    /// <summary>A count is not a whole number.</summary>
    CountMalformed,

    /// <summary><c>relations</c> is not a mapping.</summary>
    RelationsMalformed,

    /// <summary><c>relations</c> holds a kind outside the closed set.</summary>
    RelationKindUnknown,

    /// <summary>A list is not a list of scalars.</summary>
    ListMalformed,

    /// <summary>The note type or memory kind does not fit the company kind.</summary>
    MemoryKindMismatch,

    /// <summary>The path is not an allowed company path.</summary>
    PathInvalid,

    /// <summary>The <c>company/memory/&lt;folder&gt;</c> folder differs from <c>company.kind</c>.</summary>
    KindFolderMismatch,

    /// <summary>The candidate's file name differs from its note id.</summary>
    IdMismatch,

    /// <summary><c>company.statement</c> is empty.</summary>
    StatementMissing,

    /// <summary><c>applies_when</c> has no condition.</summary>
    AppliesWhenMissing,

    /// <summary>No evidence at all, and the proposer is not the owner.</summary>
    EvidenceMissing,

    /// <summary>Evidence exists but none confirms.</summary>
    ConfirmingEvidenceMissing,

    /// <summary>An evidence entry has no source id and is not a tombstone.</summary>
    EvidenceSourceMissing,

    /// <summary>The tier is outside the range allowed for the mode (T2 or T3 for admitted notes, T1 to T3 for candidates).</summary>
    TrustInvalid,

    /// <summary>The note is not active.</summary>
    StatusInvalid,

    /// <summary>A server-only field (counts, confidence, review date, admission, promotion) was set by a proposer.</summary>
    ServerFieldSetByProposer,

    /// <summary>An admitted note lacks the server-written fields.</summary>
    ServerFieldMissing,

    /// <summary>The proposer is missing or malformed.</summary>
    ProposerInvalid,

    /// <summary>A derived, supersede or relation target is not a company note id or a <c>company/</c> path.</summary>
    ReferenceNotCompany,

    /// <summary>A relation names a person.</summary>
    PersonReference,

    /// <summary>The body links to a note outside <c>company/</c>.</summary>
    BodyLinkOutsideCompany,

    /// <summary>The sanitizer found the origin project.</summary>
    SanitizerOriginProject,

    /// <summary>The sanitizer found another project name.</summary>
    SanitizerProject,

    /// <summary>The sanitizer found a customer name.</summary>
    SanitizerCustomer,

    /// <summary>The sanitizer found a person name.</summary>
    SanitizerPerson,

    /// <summary>A credential-shaped token.</summary>
    SanitizerSecret,

    /// <summary>The text claims a role, permission or approval above T1.</summary>
    AuthorityClaim,
}

/// <summary>Which stage of the pipeline a note is validated for.</summary>
public enum CompanyValidationMode
{
    /// <summary>A staged candidate under <c>company/candidates/</c>.</summary>
    Candidate,

    /// <summary>An admitted note under <c>company/memory/&lt;kind&gt;/</c>.</summary>
    Admitted,
}

/// <summary>
/// The company-area validator of docs/design/08-memory-system.md, section 6.2 rules 1 to 6. Pure: no I/O. Whether an id resolves to a
/// company note needs the Vault and is checked by the staging and admission services; here a reference only has to be shaped like a
/// company note id or a <c>company/</c> path.
/// </summary>
public static partial class CompanyNoteValidator
{
    /// <summary>The memory kind each company kind is written with. A choice of this module (doc 08 shows only <c>principle</c>, as procedural).</summary>
    public static MemoryKind MemoryKindFor(CompanyKind kind) => kind switch
    {
        CompanyKind.Learning => MemoryKind.Semantic,
        CompanyKind.WhatFails or CompanyKind.Incident => MemoryKind.Failure,
        _ => MemoryKind.Procedural,
    };

    /// <summary>Checks <paramref name="note"/> at <paramref name="path"/> and returns every finding, empty when valid.</summary>
    public static IReadOnlyList<CompanyFinding> Validate(CompanyNote note, string path, CompanyValidationMode mode, HiveSanitizerTerms terms)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(terms);

        var found = new HashSet<CompanyFinding>();
        var c = note.Company;
        var m = note.Memory;

        CheckPath(note, path, mode, found);

        if (m.Kind != MemoryKindFor(c.Kind) || !string.Equals(m.Type, MemoryNoteTypes.TypeFor(m.Kind), StringComparison.Ordinal))
        {
            found.Add(CompanyFinding.MemoryKindMismatch);
        }

        if (m.Status != MemoryNoteStatus.Active)
        {
            found.Add(CompanyFinding.StatusInvalid);
        }

        var minTier = mode == CompanyValidationMode.Admitted ? MemoryTier.AgentVerified : MemoryTier.UntrustedDerived;
        if (m.Trust < minTier || m.Trust > MemoryTier.OwnerPromoted)
        {
            found.Add(CompanyFinding.TrustInvalid);
        }

        // Rule 1.
        if (string.IsNullOrWhiteSpace(c.Statement))
        {
            found.Add(CompanyFinding.StatementMissing);
        }

        if (!c.AppliesWhen.Any(s => !string.IsNullOrWhiteSpace(s)))
        {
            found.Add(CompanyFinding.AppliesWhenMissing);
        }

        var ownerWritten = string.Equals(c.ProposedBy, "owner", StringComparison.Ordinal);
        if (c.Evidence.Count == 0)
        {
            if (!ownerWritten)
            {
                found.Add(CompanyFinding.EvidenceMissing);
            }
        }
        else if (!c.Evidence.Any(e => e.Role == CompanyEvidenceRole.Confirms))
        {
            found.Add(CompanyFinding.ConfirmingEvidenceMissing);
        }

        if (c.Evidence.Any(e => e.Status != CompanyEvidenceStatus.Tombstone && string.IsNullOrWhiteSpace(e.DocId)))
        {
            found.Add(CompanyFinding.EvidenceSourceMissing);
        }

        if (c.ProposedBy is null || !(ownerWritten || ProposerRegex().IsMatch(c.ProposedBy)))
        {
            found.Add(CompanyFinding.ProposerInvalid);
        }

        // Rule 2.
        foreach (var target in m.DerivedFrom.Concat(m.Supersedes).Concat(c.Relations.ConditionedOn).Concat(c.Relations.Affects).Concat(c.Relations.ResolvedBy))
        {
            if (PersonRegex().IsMatch(target))
            {
                found.Add(CompanyFinding.PersonReference);
            }
            else if (!IsCompanyReference(target))
            {
                found.Add(CompanyFinding.ReferenceNotCompany);
            }
        }

        // Rule 3.
        foreach (Match link in WikiLinkRegex().Matches(note.Body))
        {
            var target = link.Groups[1].Value.Split('#', '|')[0].Trim();
            if (target.Length > 0 && !target.StartsWith("company/", StringComparison.Ordinal))
            {
                found.Add(CompanyFinding.BodyLinkOutsideCompany);
            }
        }

        // Rule 4: title, statement, conditions and body.
        var text = string.Join('\n', new[] { c.Statement }.Concat(c.AppliesWhen).Concat(c.NotWhen).Append(note.Body));
        foreach (var finding in HiveSanitizer.Check(m.Title, text, terms).Findings)
        {
            found.Add(finding switch
            {
                HiveSanitizerFinding.OriginProject => CompanyFinding.SanitizerOriginProject,
                HiveSanitizerFinding.Project => CompanyFinding.SanitizerProject,
                HiveSanitizerFinding.Customer => CompanyFinding.SanitizerCustomer,
                HiveSanitizerFinding.Person => CompanyFinding.SanitizerPerson,
                _ => CompanyFinding.SanitizerSecret,
            });
        }

        if (m.Trust > MemoryTier.UntrustedDerived && AuthorityClaimScanner.ClaimsAuthority(m.Title + "\n" + text))
        {
            found.Add(CompanyFinding.AuthorityClaim);
        }

        // Rule 6, and the server-written fields of an admitted note.
        var serverFields = c.Counts is not null || c.CountsApplied is not null || c.Confidence is not null || c.ReviewBy is not null
            || c.Admitted is not null || c.Promoted is not null;
        if (mode == CompanyValidationMode.Candidate)
        {
            if (serverFields)
            {
                found.Add(CompanyFinding.ServerFieldSetByProposer);
            }
        }
        else if (c.Counts is null || c.Confidence is null || c.ReviewBy is null || c.Admitted is null
            || (m.Trust == MemoryTier.OwnerPromoted) != (c.Promoted is not null))
        {
            found.Add(CompanyFinding.ServerFieldMissing);
        }

        return [.. found.Order()];
    }

    /// <summary>True when <paramref name="target"/> is shaped like a company note id (a 26-character ULID) or a <c>company/</c> Markdown path.</summary>
    public static bool IsCompanyReference(string target) =>
        UlidRegex().IsMatch(target) || (target.StartsWith("company/", StringComparison.Ordinal) && PathRegex().IsMatch(target));

    private static void CheckPath(CompanyNote note, string path, CompanyValidationMode mode, HashSet<CompanyFinding> found)
    {
        var parts = path.Split('/');
        if (mode == CompanyValidationMode.Candidate)
        {
            if (parts.Length != 3 || parts[0] != "company" || parts[1] != "candidates" || !path.EndsWith(".md", StringComparison.Ordinal)
                || !UlidRegex().IsMatch(parts[2][..^3]))
            {
                found.Add(CompanyFinding.PathInvalid);
            }
            else if (!string.Equals(parts[2][..^3], note.Memory.Id, StringComparison.Ordinal))
            {
                found.Add(CompanyFinding.IdMismatch);
            }

            return;
        }

        if (parts.Length != 4 || parts[0] != "company" || parts[1] != "memory" || !parts[3].EndsWith(".md", StringComparison.Ordinal)
            || !SlugRegex().IsMatch(parts[3][..^3]) || !CompanyKinds.TryParse(parts[2], out _))
        {
            found.Add(CompanyFinding.PathInvalid);
        }
        else if (!string.Equals(parts[2], CompanyKinds.Name(note.Company.Kind), StringComparison.Ordinal))
        {
            found.Add(CompanyFinding.KindFolderMismatch);
        }
    }

    [GeneratedRegex("^[0-9A-Za-z]{26}$")]
    private static partial Regex UlidRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex SlugRegex();

    [GeneratedRegex(@"^company/[A-Za-z0-9._/-]+\.md$")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"^agent:[A-Za-z0-9._-]+$")]
    private static partial Regex ProposerRegex();

    [GeneratedRegex(@"^((person|agent|user|member|human):|@)", RegexOptions.IgnoreCase)]
    private static partial Regex PersonRegex();

    [GeneratedRegex(@"!?\[\[([^\]\n]*)\]\]")]
    private static partial Regex WikiLinkRegex();
}
