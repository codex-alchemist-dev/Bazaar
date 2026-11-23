using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// Genetic Memory Transfer (docs/design/25-company-systems-knowledge.md work package 9, doc 07). Candidates wait in
/// the ledger, outside the Vault, so retrieval cannot see them. On accept a lesson is written through
/// <see cref="IGmtLessonWriter"/> (the memory write path) as a note in the receiving agent's own memory, labelled with its origin and never above T2:
/// a tainted child's lesson stays T1, and only an owner promotion raises trust. Tool grants and charter text are queued for
/// the owner and write nothing here; the approval ladder applies them.
/// </summary>
public sealed partial class GeneticMemoryService : IGeneticMemoryTransfer
{
    private readonly IGmtLedger _ledger;
    private readonly IGmtLessonWriter _writer;
    private readonly IGmtSecretScanner _secrets;
    private readonly GmtOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public GeneticMemoryService(
        IGmtLedger ledger, IGmtLessonWriter writer, IGmtSecretScanner secrets, GmtOptions? options = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(secrets);
        _ledger = ledger;
        _writer = writer;
        _secrets = secrets;
        _options = options ?? new GmtOptions();
        _time = time ?? TimeProvider.System;
    }

    [GeneratedRegex("^[A-Za-z0-9_./@+:-]+$")]
    private static partial Regex PlainScalar();

    /// <inheritdoc />
    public async Task<GmtOutcome> StageAsync(GmtStageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ChildAgent) || string.IsNullOrWhiteSpace(request.TargetAgent)
            || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
        {
            return GmtOutcome.Refused(GmtRefusal.InvalidInput);
        }

        if (request.ContentClass is not (GmtContentClass.Lesson or GmtContentClass.ToolGrant or GmtContentClass.CharterText))
        {
            return GmtOutcome.Refused(GmtRefusal.NotTransferable, request.ContentClass.ToString());
        }

        if (string.Equals(request.ChildAgent, request.TargetAgent, StringComparison.Ordinal))
        {
            return GmtOutcome.Refused(GmtRefusal.SelfTransfer);
        }

        if (request.ChildQuarantined)
        {
            return GmtOutcome.Refused(GmtRefusal.ChildQuarantined);
        }

        if (request.TraceRef is not null && !PlainScalar().IsMatch(request.TraceRef))
        {
            return GmtOutcome.Refused(GmtRefusal.InvalidInput, "trace");
        }

        var findings = GmtScreen.Check(request.Title, request.Body, request.Terms, _secrets);
        if (findings.Count > 0)
        {
            return GmtOutcome.Refused(FindingRefusal(findings), string.Join(',', findings));
        }

        var existing = await _ledger.ListAsync(ct).ConfigureAwait(false);
        if (existing.Any(c => c.Status == GmtStatus.Staged
            && c.ChildAgent == request.ChildAgent
            && c.TargetAgent == request.TargetAgent
            && c.ContentClass == request.ContentClass
            && string.Equals(Slugify(c.Title), Slugify(request.Title), StringComparison.Ordinal)))
        {
            return GmtOutcome.Refused(GmtRefusal.Duplicate);
        }

        var candidate = new GmtCandidate(
            Guid.NewGuid(), request.ChildAgent, request.TargetAgent, request.ContentClass, request.Kind,
            request.Title.Trim(), request.Body, request.TraceRef, request.ChildTainted,
            RequiredFor(request.ContentClass), GmtStatus.Staged, _time.GetUtcNow());
        await _ledger.AddAsync(candidate, ct).ConfigureAwait(false);
        return GmtOutcome.Ok(candidate.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GmtCandidate>> QueueAsync(GmtReviewer role, CancellationToken ct) =>
        [.. (await _ledger.ListAsync(ct).ConfigureAwait(false)).Where(c => c.Status == GmtStatus.Staged && MayDecide(role, c))];

    /// <inheritdoc />
    public async Task<GmtOutcome> RejectAsync(Guid id, GmtReviewer role, CancellationToken ct)
    {
        var (candidate, refusal) = await LoadForDecisionAsync(id, role, ct).ConfigureAwait(false);
        if (candidate is null)
        {
            return refusal!;
        }

        await _ledger.UpdateAsync(candidate with { Status = GmtStatus.Rejected }, ct).ConfigureAwait(false);
        return GmtOutcome.Ok(id);
    }

    /// <inheritdoc />
    public async Task<GmtOutcome> AcceptAsync(Guid id, GmtReviewer role, string? editedTitle, string? editedBody, CancellationToken ct)
    {
        var (candidate, refusal) = await LoadForDecisionAsync(id, role, ct).ConfigureAwait(false);
        if (candidate is null)
        {
            return refusal!;
        }

        var title = string.IsNullOrWhiteSpace(editedTitle) ? candidate.Title : editedTitle.Trim();
        var body = string.IsNullOrWhiteSpace(editedBody) ? candidate.Body : editedBody;

        // An edit is screened again, with the same checks as staging; a secret is the one thing a reviewer's edit may not add.
        if (_secrets.ContainsSecret(title) || _secrets.ContainsSecret(body))
        {
            return GmtOutcome.Refused(GmtRefusal.SecretFound, nameof(GmtFinding.Secret), id);
        }

        if (candidate.ContentClass != GmtContentClass.Lesson)
        {
            // Grants and charter text change through the approval ladder, never as a memory note.
            await _ledger.UpdateAsync(candidate with { Title = title, Body = body, Status = GmtStatus.Accepted }, ct).ConfigureAwait(false);
            return GmtOutcome.Ok(id);
        }

        var path = await ResolvePathAsync(candidate, title, ct).ConfigureAwait(false);
        var write = new GmtLessonWrite(candidate.Id, path, candidate.Kind, title, body, candidate.ChildAgent, candidate.TraceRef, candidate.Tainted);
        var result = await _writer.WriteAsync(write, _options.WriterAgentId, ct).ConfigureAwait(false);
        if (result.Status is GmtWriteStatus.PathInvalid or GmtWriteStatus.NoteInvalid)
        {
            return GmtOutcome.Refused(GmtRefusal.PathInvalid, null, id);
        }

        if (result.Status != GmtWriteStatus.Written)
        {
            return GmtOutcome.Refused(GmtRefusal.CommitFailed, result.Detail, id);
        }

        await _ledger.UpdateAsync(
            candidate with { Title = title, Body = body, Status = GmtStatus.Accepted, AcceptedPath = path }, ct).ConfigureAwait(false);
        return GmtOutcome.Ok(id, path);
    }

    private GmtReviewer RequiredFor(GmtContentClass contentClass) =>
        contentClass == GmtContentClass.Lesson ? _options.LessonReviewer : GmtReviewer.Owner;

    // The owner may decide anything; the architect only what the setting routes to the architect.
    private static bool MayDecide(GmtReviewer role, GmtCandidate candidate) =>
        role == GmtReviewer.Owner || candidate.RequiredReviewer == GmtReviewer.Architect;

    private async Task<(GmtCandidate? Candidate, GmtOutcome? Refusal)> LoadForDecisionAsync(Guid id, GmtReviewer role, CancellationToken ct)
    {
        var candidate = await _ledger.GetAsync(id, ct).ConfigureAwait(false);
        if (candidate is null)
        {
            return (null, GmtOutcome.Refused(GmtRefusal.NotFound, null, id));
        }

        if (candidate.Status != GmtStatus.Staged)
        {
            return (null, GmtOutcome.Refused(GmtRefusal.AlreadyDecided, null, id));
        }

        // The floor: tool grants and charter text are owner-only, whatever the setting says.
        if (!MayDecide(role, candidate))
        {
            return (null, GmtOutcome.Refused(GmtRefusal.ReviewerNotAllowed, null, id));
        }

        return (candidate, null);
    }

    private static GmtRefusal FindingRefusal(IReadOnlyList<GmtFinding> findings) =>
        findings.Contains(GmtFinding.Secret) ? GmtRefusal.SecretFound : GmtRefusal.PersonalData;

    private async Task<string> ResolvePathAsync(GmtCandidate candidate, string title, CancellationToken ct)
    {
        var folder = candidate.Kind == GmtLessonKind.Failure ? "failures" : "lessons";
        var slug = Slugify(title);
        var plain = $"agents/{candidate.TargetAgent}/memory/{folder}/{slug}.md";

        var taken = (await _ledger.ListAsync(ct).ConfigureAwait(false))
            .Any(c => c.Id != candidate.Id && string.Equals(c.AcceptedPath, plain, StringComparison.Ordinal));
        if (!taken)
        {
            return plain;
        }

        var suffix = "-" + Convert.ToHexString(candidate.Id.ToByteArray())[..8].ToLowerInvariant();
        var stem = slug.Length + suffix.Length > 64 ? slug[..(64 - suffix.Length)].TrimEnd('-') : slug;
        return $"agents/{candidate.TargetAgent}/memory/{folder}/{stem}{suffix}.md";
    }

    private static string Slugify(string title)
    {
        var sb = new StringBuilder();
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

        return slug.Length == 0 ? "lesson" : slug;
    }
}
