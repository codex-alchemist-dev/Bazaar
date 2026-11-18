using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Memory.Notes;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Memory.Write;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Hive;

/// <summary>What a lesson's source is. Only <see cref="Lesson"/> may be copied.</summary>
public enum HiveSourceClass
{
    /// <summary>A procedural lesson, failure lesson or technical fact from a memory note.</summary>
    Lesson,

    /// <summary>A preference of an agent or the owner.</summary>
    Preference,

    /// <summary>A note that stays in private memory only.</summary>
    PrivateMemoryOnly,

    /// <summary>GC message text.</summary>
    GcText,

    /// <summary>Other Vault text that is not a memory lesson.</summary>
    VaultText,

    /// <summary>Reputation data.</summary>
    Reputation,
}

/// <summary>A request to copy one lesson for one target project.</summary>
/// <param name="Lesson">The lesson.</param>
/// <param name="TargetProject">The target project the grant covers.</param>
/// <param name="SourceText">Full text of the source note.</param>
/// <param name="SourceClass">What the source is.</param>
/// <param name="Terms">Names the copy must not contain.</param>
public sealed record HiveCopyRequest(
    LessonRef Lesson,
    string TargetProject,
    string SourceText,
    HiveSourceClass SourceClass,
    HiveSanitizerTerms Terms);

/// <summary>Result of revoking a grant.</summary>
/// <param name="DeprecatedPaths">Copies set to <c>deprecated</c>. Files are never deleted.</param>
/// <param name="KeptPaths">Copies left active because another grant still covers them.</param>
/// <param name="CommitFailure">Why the deprecation commit failed, or null. The grant is removed either way.</param>
public sealed record HiveRevokeResult(
    IReadOnlyList<string> DeprecatedPaths,
    IReadOnlyList<string> KeptPaths,
    string? CommitFailure);

/// <summary>
/// The Hive copy flow (docs/design/08-memory-system.md, section 6; doc 25). Checks eligibility, sanitizes, holds the
/// copy for <c>hive.grant_review_days</c>, then writes <c>hive/&lt;kind&gt;/&lt;slug&gt;.md</c> through
/// <see cref="IMemoryBatchSink"/>. Revoking a grant deprecates copies and never deletes them.
/// </summary>
public sealed partial class HiveCopyService
{
    private readonly IHiveGrantPolicy _policy;
    private readonly IHiveGrantStore _grants;
    private readonly IHiveCopyLedger _ledger;
    private readonly IHiveNoteReader _reader;
    private readonly IMemoryBatchSink _sink;
    private readonly HiveCopyOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    public HiveCopyService(
        IHiveGrantPolicy policy,
        IHiveGrantStore grants,
        IHiveCopyLedger ledger,
        IHiveNoteReader reader,
        IMemoryBatchSink sink,
        HiveCopyOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sink);
        _policy = policy;
        _grants = grants;
        _ledger = ledger;
        _reader = reader;
        _sink = sink;
        _options = options ?? new HiveCopyOptions();
        _time = time ?? TimeProvider.System;
    }

    [GeneratedRegex("^[A-Za-z0-9_./@+:-]+$")]
    private static partial Regex PlainScalar();

    /// <summary>Checks a lesson and holds an eligible, sanitized copy for the review window.</summary>
    public async Task<HiveCopyOutcome> SubmitAsync(HiveCopyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lesson = request.Lesson;

        if (request.SourceClass != HiveSourceClass.Lesson)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.ClassNotShareable, request.SourceClass.ToString());
        }

        var parsed = NoteDocument.Parse(request.SourceText);
        if (parsed.Document is not { } doc)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.Unparseable);
        }

        var note = doc.Note;
        var expected = note.Kind switch
        {
            MemoryKind.Procedural => (HiveKind?)HiveKind.Procedure,
            MemoryKind.Failure => HiveKind.Failure,
            MemoryKind.Semantic => HiveKind.TechnicalFact,
            _ => null,
        };
        if (expected is null)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.ClassNotShareable, note.Kind.ToString());
        }

        if (expected != lesson.Kind)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.KindMismatch);
        }

        if (note.Trust < MemoryTier.AgentVerified)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.TierTooLow, MemoryNoteWireName(note.Trust));
        }

        if (note.Tainted || note.SourceTrust is ContentTrust.External or ContentTrust.Quarantined)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.TaintedSource);
        }

        if (note.Status != MemoryNoteStatus.Active)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.NotActive);
        }

        if (note.Subjects.Count > 0)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.SubjectTagged);
        }

        if (!_options.AllowedKinds.Contains(lesson.Kind))
        {
            return HiveCopyOutcome.Refused(HiveRefusal.KindNotAllowed);
        }

        if (!await _policy.MayCopyAsync(lesson, request.TargetProject, ct).ConfigureAwait(false))
        {
            return HiveCopyOutcome.Refused(HiveRefusal.NoGrant);
        }

        var terms = request.Terms with { OriginProject = lesson.OriginProject };
        var verdict = HiveSanitizer.Check(note.Title, doc.Body, terms);
        if (!verdict.IsClean)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.SanitizerRejected, string.Join(',', verdict.Findings));
        }

        var slug = Slugify(note.Title);
        if (!MemoryAreas.CheckPath(PathFor(lesson.Kind, slug), MemoryKind.Hive).IsValid)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.PathInvalid);
        }

        if (await IsDuplicateAsync(lesson, request.TargetProject, ct).ConfigureAwait(false))
        {
            return HiveCopyOutcome.Refused(HiveRefusal.Duplicate);
        }

        var now = _time.GetUtcNow();
        DateTimeOffset? releaseAt = _options.ReviewDays == 0 ? null : now.AddDays(_options.ReviewDays);
        var pending = new HivePendingCopy(
            Guid.NewGuid(), lesson, request.TargetProject, slug, note.Title, doc.Body,
            MemoryNoteWireName(note.Trust), now, releaseAt);
        await _ledger.AddPendingAsync(pending, ct).ConfigureAwait(false);
        return HiveCopyOutcome.Held(pending.Id, PathFor(lesson.Kind, slug), releaseAt);
    }

    /// <summary>
    /// Releases held copies whose review window ended, in one commit. With <c>hive.grant_review_days = 0</c> no copy
    /// has a release time, so nothing is ever released here.
    /// </summary>
    public async Task<IReadOnlyList<HiveCopyOutcome>> ReleaseDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var due = (await _ledger.ListPendingAsync(ct).ConfigureAwait(false))
            .Where(p => p.ReleaseAt is { } at && at <= now)
            .ToList();
        return await ReleaseAsync(due, ct).ConfigureAwait(false);
    }

    /// <summary>Owner approval: releases one held copy now, whatever its window.</summary>
    public async Task<HiveCopyOutcome> ApproveAsync(Guid pendingId, CancellationToken ct)
    {
        var pending = (await _ledger.ListPendingAsync(ct).ConfigureAwait(false)).FirstOrDefault(p => p.Id == pendingId);
        if (pending is null)
        {
            return HiveCopyOutcome.Refused(HiveRefusal.NotFound);
        }

        return (await ReleaseAsync([pending], ct).ConfigureAwait(false))[0];
    }

    /// <summary>Owner rejection: drops a held copy. Returns false when it is not held.</summary>
    public Task<bool> RejectAsync(Guid pendingId, CancellationToken ct) => _ledger.RemovePendingAsync(pendingId, ct);

    /// <summary>
    /// Removes the grant, drops held copies under it and sets the front matter of written copies to
    /// <c>deprecated</c> through the writer. Files are never deleted. A copy another grant still covers stays active.
    /// </summary>
    public async Task<HiveRevokeResult> RevokeAsync(
        string fromProject, string toProject, HiveKind kind, string revokedBy, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromProject);
        ArgumentException.ThrowIfNullOrEmpty(toProject);
        ArgumentException.ThrowIfNullOrEmpty(revokedBy);

        await _grants.RemoveAsync(fromProject, toProject, kind, ct).ConfigureAwait(false);

        foreach (var held in await _ledger.ListPendingAsync(ct).ConfigureAwait(false))
        {
            if (InGrant(held.Lesson, held.TargetProject, fromProject, toProject, kind))
            {
                await _ledger.RemovePendingAsync(held.Id, ct).ConfigureAwait(false);
            }
        }

        var copies = await _ledger.ListCopiesAsync(ct).ConfigureAwait(false);
        var affected = copies.Where(c => !c.Revoked && InGrant(c.Lesson, c.TargetProject, fromProject, toProject, kind)).ToList();
        var deprecated = new List<string>();
        var kept = new List<string>();
        var items = new List<MemoryWriteItem>();

        foreach (var path in affected.Select(c => c.Path).Distinct(StringComparer.Ordinal))
        {
            var stillCovered = copies.Any(c => !c.Revoked && string.Equals(c.Path, path, StringComparison.Ordinal) && !affected.Contains(c));
            if (stillCovered)
            {
                kept.Add(path);
                continue;
            }

            var text = await _reader.ReadAsync(path, ct).ConfigureAwait(false);
            if (text is null || NoteDocument.Parse(text).Document is not { } doc)
            {
                continue;
            }

            deprecated.Add(path);
            if (doc.Note.Status == MemoryNoteStatus.Deprecated)
            {
                continue;
            }

            var next = doc.Note with
            {
                Status = MemoryNoteStatus.Deprecated,
                Version = doc.Note.Version + 1,
                Deprecated = new MemoryDeprecation(
                    "hive grant revoked", revokedBy, _time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)),
            };
            items.Add(new MemoryWriteItem(Guid.NewGuid(), path, doc.WithNote(next).Serialize(), null, null, "hive-revoke"));
        }

        string? failure = null;
        if (items.Count > 0)
        {
            var result = await _sink.CommitAsync(MemoryBatch.Create(_options.WriterAgentId, items), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                failure = result.FailureReason;
            }
        }

        if (failure is null)
        {
            await _ledger.MarkRevokedAsync(fromProject, toProject, kind, ct).ConfigureAwait(false);
        }

        return new HiveRevokeResult(failure is null ? deprecated : [], kept, failure);
    }

    private static bool InGrant(LessonRef lesson, string target, string from, string to, HiveKind kind) =>
        lesson.Kind == kind
        && string.Equals(lesson.OriginProject, from, StringComparison.Ordinal)
        && string.Equals(target, to, StringComparison.Ordinal);

    private async Task<bool> IsDuplicateAsync(LessonRef lesson, string target, CancellationToken ct)
    {
        var pending = await _ledger.ListPendingAsync(ct).ConfigureAwait(false);
        if (pending.Any(p => SameLesson(p.Lesson, lesson) && string.Equals(p.TargetProject, target, StringComparison.Ordinal)))
        {
            return true;
        }

        var copies = await _ledger.ListCopiesAsync(ct).ConfigureAwait(false);
        return copies.Any(c => !c.Revoked && SameLesson(c.Lesson, lesson) && string.Equals(c.TargetProject, target, StringComparison.Ordinal));
    }

    private static bool SameLesson(LessonRef a, LessonRef b) =>
        string.Equals(a.OriginProject, b.OriginProject, StringComparison.Ordinal)
        && string.Equals(a.OriginNote, b.OriginNote, StringComparison.Ordinal);

    private async Task<IReadOnlyList<HiveCopyOutcome>> ReleaseAsync(List<HivePendingCopy> batch, CancellationToken ct)
    {
        var outcomes = new HiveCopyOutcome[batch.Count];
        var copies = (await _ledger.ListCopiesAsync(ct).ConfigureAwait(false)).ToList();
        var claimed = new Dictionary<string, LessonRef>(StringComparer.Ordinal);
        var writes = new List<(int Index, HivePendingCopy Pending, string Path, MemoryWriteItem? Item)>();

        for (var i = 0; i < batch.Count; i++)
        {
            var p = batch[i];

            // Re-check at release: the grant may have been revoked or the kinds tightened during the window.
            if (!await _policy.MayCopyAsync(p.Lesson, p.TargetProject, ct).ConfigureAwait(false))
            {
                await _ledger.RemovePendingAsync(p.Id, ct).ConfigureAwait(false);
                outcomes[i] = HiveCopyOutcome.Refused(HiveRefusal.NoGrant);
                continue;
            }

            if (SecretScanner.ContainsSecret(p.Title) || SecretScanner.ContainsSecret(p.Body))
            {
                await _ledger.RemovePendingAsync(p.Id, ct).ConfigureAwait(false);
                outcomes[i] = HiveCopyOutcome.Refused(HiveRefusal.SanitizerRejected, nameof(HiveSanitizerFinding.Secret));
                continue;
            }

            var path = ResolvePath(p, copies, claimed);
            if (!MemoryAreas.CheckPath(path, MemoryKind.Hive).IsValid)
            {
                await _ledger.RemovePendingAsync(p.Id, ct).ConfigureAwait(false);
                outcomes[i] = HiveCopyOutcome.Refused(HiveRefusal.PathInvalid);
                continue;
            }

            claimed[path] = p.Lesson;
            var live = copies.Exists(c => !c.Revoked && string.Equals(c.Path, path, StringComparison.Ordinal) && SameLesson(c.Lesson, p.Lesson));
            if (live && await _reader.ReadAsync(path, ct).ConfigureAwait(false) is not null)
            {
                writes.Add((i, p, path, null));
                continue;
            }

            var previous = await _reader.ReadAsync(path, ct).ConfigureAwait(false);
            long version = 1;
            if (previous is not null && NoteDocument.Parse(previous).Document is { } old)
            {
                version = old.Note.Version + 1;
            }

            var content = BuildCopy(p, version);
            if (content is null)
            {
                await _ledger.RemovePendingAsync(p.Id, ct).ConfigureAwait(false);
                outcomes[i] = HiveCopyOutcome.Refused(HiveRefusal.Unparseable, "copy-build");
                continue;
            }

            writes.Add((i, p, path, new MemoryWriteItem(Guid.NewGuid(), path, content, null, null, "hive-copy: sanitizer clean")));
        }

        var items = writes.Where(w => w.Item is not null).Select(w => w.Item!).ToList();
        string? failure = null;
        if (items.Count > 0)
        {
            var result = await _sink.CommitAsync(MemoryBatch.Create(_options.WriterAgentId, items), ct).ConfigureAwait(false);
            failure = result.Succeeded ? null : result.FailureReason;
        }

        foreach (var (index, pending, path, item) in writes)
        {
            if (failure is not null && item is not null)
            {
                outcomes[index] = HiveCopyOutcome.Refused(HiveRefusal.CommitFailed, failure);
                continue;
            }

            await _ledger.RecordCopyAsync(new HiveCopyRecord(path, pending.Lesson, pending.TargetProject, false), ct).ConfigureAwait(false);
            await _ledger.RemovePendingAsync(pending.Id, ct).ConfigureAwait(false);
            outcomes[index] = HiveCopyOutcome.Copied(path);
        }

        return outcomes;
    }

    private static string ResolvePath(HivePendingCopy p, List<HiveCopyRecord> copies, Dictionary<string, LessonRef> claimed)
    {
        var plain = PathFor(p.Lesson.Kind, p.Slug);
        if (!Taken(plain, p.Lesson, copies, claimed))
        {
            return plain;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Lesson.OriginProject + "\n" + p.Lesson.OriginNote)))[..8].ToLowerInvariant();
        var suffix = "-" + hash;
        var stem = p.Slug.Length + suffix.Length > 64 ? p.Slug[..(64 - suffix.Length)].TrimEnd('-') : p.Slug;
        return PathFor(p.Lesson.Kind, stem + suffix);
    }

    private static bool Taken(string path, LessonRef lesson, List<HiveCopyRecord> copies, Dictionary<string, LessonRef> claimed) =>
        (claimed.TryGetValue(path, out var other) && !SameLesson(other, lesson))
        || copies.Exists(c => string.Equals(c.Path, path, StringComparison.Ordinal) && !SameLesson(c.Lesson, lesson));

    private string? BuildCopy(HivePendingCopy p, long version)
    {
        var originTier = MemoryNoteWireNameToTier(p.OriginTier);
        var tier = originTier > _options.MaxCopyTier ? _options.MaxCopyTier : originTier;
        var idSource = Encoding.UTF8.GetBytes(p.Lesson.OriginProject + "\n" + p.Lesson.OriginNote);
        var id = Convert.ToHexString(SHA256.HashData(idSource))[..26];

        var note = new MemoryNote(
            id,
            MemoryNoteTypes.TypeFor(MemoryKind.Hive),
            p.Title,
            null,
            MemoryKind.Hive,
            MemoryNoteStatus.Active,
            version,
            tier,
            ContentTrust.Agent,
            false,
            null,
            [],
            [],
            null,
            []);

        var extras = new (string Key, string Value)[]
        {
            ("origin_project", p.Lesson.OriginProject),
            ("origin_note", p.Lesson.OriginNote),
            ("trust", p.OriginTier),
            ("origin_kind", HiveKinds.Name(p.Lesson.Kind)),
        };
        if (extras.Any(e => !PlainScalar().IsMatch(e.Value)))
        {
            return null;
        }

        var head = NoteDocument.Create(note, string.Empty).Serialize();
        var close = head.LastIndexOf("---\n", StringComparison.Ordinal);
        var sb = new StringBuilder(head[..close]);
        foreach (var (key, value) in extras)
        {
            sb.Append(key).Append(": ").Append(value).Append('\n');
        }

        var text = sb.Append("---\n").Append(p.Body).ToString();
        return NoteDocument.Parse(text).Document is null ? null : text;
    }

    private static string PathFor(HiveKind kind, string slug) => "hive/" + HiveKinds.Name(kind) + "/" + slug + ".md";

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

    private static string MemoryNoteWireName(MemoryTier tier) => "T" + ((int)tier).ToString(CultureInfo.InvariantCulture);

    private static MemoryTier MemoryNoteWireNameToTier(string name) =>
        int.TryParse(name.AsSpan(1), CultureInfo.InvariantCulture, out var n) && Enum.IsDefined((MemoryTier)n)
            ? (MemoryTier)n
            : MemoryTier.Quarantined;
}
