using Bazaar.Server.Hive.Abstractions;

namespace Bazaar.Server.Hive;

public enum HiveSubmitOutcome
{
    /// <summary>Sanitised and held in the review queue. Not in the Vault yet.</summary>
    Held = 0,
    Refused,
    NoGrant,
    SanitizerRejected,
    /// <summary>A copy of this memory into this project already exists (in any state); nothing was added.</summary>
    AlreadyCopied,
}

public sealed record HiveSubmitResult(HiveSubmitOutcome Outcome, HiveRefusal Refusal, string? Reason, HiveCopy? Copy);

/// <summary>
/// The copy flow (doc 25 Hive row, doc 08 section 6): eligible lesson, then grant check, then sanitizer, then held for
/// <c>hive.grant_review_days</c>, then written to the Vault under the grant. A copy never promotes itself: it carries
/// at most T2, and only an owner promotion in the Vault raises trust. A copy is text, never a reference into another project's
/// Vault or GC text.
/// </summary>
public sealed class HiveCopyService(
    IHiveGrantStore grants,
    IHiveCopyStore copies,
    IHiveSanitizer sanitizer,
    IHiveVaultWriter writer,
    IHiveAudit audit,
    IHiveSettings settings,
    TimeProvider clock)
{
    /// <summary>Processes one lesson. A nightly batch calls this per eligible lesson, then <see cref="ReleaseDueAsync"/>.</summary>
    public async Task<HiveSubmitResult> SubmitAsync(LessonRef lesson, ProjectId target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lesson);
        var options = await settings.GetAsync(ct).ConfigureAwait(false);

        var refusal = HiveEligibility.Check(lesson, target, options);
        if (refusal != HiveRefusal.None)
            return new HiveSubmitResult(HiveSubmitOutcome.Refused, refusal, refusal.ToString(), null);

        if (!await grants.ExistsAsync(lesson.Project, target, lesson.Kind, ct).ConfigureAwait(false))
            return new HiveSubmitResult(HiveSubmitOutcome.NoGrant, HiveRefusal.None, "No grant for this project pair and kind.", null);

        if (await copies.FindAsync(lesson.Project, lesson.MemoryId, target, ct).ConfigureAwait(false) is { } existing)
            return new HiveSubmitResult(HiveSubmitOutcome.AlreadyCopied, HiveRefusal.None, null, existing);

        var sanitized = await sanitizer.CheckAsync(lesson.Title, lesson.Body, ct).ConfigureAwait(false);
        if (!sanitized.Accepted)
            return new HiveSubmitResult(HiveSubmitOutcome.SanitizerRejected, HiveRefusal.None, sanitized.Reason ?? "Rejected by the sanitizer.", null);

        var now = clock.GetUtcNow();
        // 0 (or negative) keeps the batch in review until the owner approves; a positive window then releases it automatically.
        DateTimeOffset? reviewUntil = options.GrantReviewDays > 0 ? now.AddDays(options.GrantReviewDays) : null;
        var copy = new HiveCopy(
            Guid.CreateVersion7(),
            lesson.Project,
            target,
            lesson.Kind,
            lesson.MemoryId,
            lesson.OriginKind,
            lesson.Trust > HiveTrust.T2 ? HiveTrust.T2 : lesson.Trust,
            lesson.Title,
            lesson.Body,
            HiveDocument.PathFor(lesson.Kind, lesson.Title, lesson.Project, lesson.MemoryId, target),
            HiveCopyState.InReview,
            now,
            reviewUntil,
            null,
            null);

        if (!await copies.TryInsertAsync(copy, ct).ConfigureAwait(false))
        {
            var raced = await copies.FindAsync(lesson.Project, lesson.MemoryId, target, ct).ConfigureAwait(false);
            return new HiveSubmitResult(HiveSubmitOutcome.AlreadyCopied, HiveRefusal.None, null, raced);
        }

        await audit.AppendAsync(new HiveAuditEvent(now, "hive", "hive.copy.held", $"copy={copy.CopyId} path={copy.Path}"), ct).ConfigureAwait(false);
        return new HiveSubmitResult(HiveSubmitOutcome.Held, HiveRefusal.None, null, copy);
    }

    /// <summary>
    /// Releases copies whose review window ended, provided the grant still exists (otherwise they are deprecated unwritten). With
    /// <c>hive.grant_review_days = 0</c> nothing is released automatically. Returns the number written to the Vault.
    /// </summary>
    public async Task<int> ReleaseDueAsync(CancellationToken ct)
    {
        var options = await settings.GetAsync(ct).ConfigureAwait(false);
        if (options.GrantReviewDays <= 0)
            return 0;

        var now = clock.GetUtcNow();
        var released = 0;
        foreach (var copy in await copies.ListDueAsync(now, ct).ConfigureAwait(false))
        {
            if (await ReleaseAsync(copy, "hive", now, options, ct).ConfigureAwait(false))
                released++;
        }

        return released;
    }

    /// <summary>The owner approves one held copy before its window ends (or when the window is 0). False when it is not in review or no longer eligible.</summary>
    public async Task<bool> ApproveAsync(Guid copyId, string approvedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(approvedBy))
            throw new ArgumentException("An approval records who approved it.", nameof(approvedBy));
        var copy = await copies.GetAsync(copyId, ct).ConfigureAwait(false);
        if (copy is not { State: HiveCopyState.InReview })
            return false;
        var options = await settings.GetAsync(ct).ConfigureAwait(false);
        return await ReleaseAsync(copy, approvedBy, clock.GetUtcNow(), options, ct).ConfigureAwait(false);
    }

    /// <summary>The owner rejects a held copy. It is deprecated without ever reaching the Vault.</summary>
    public async Task<bool> RejectAsync(Guid copyId, string rejectedBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rejectedBy))
            throw new ArgumentException("A rejection records who rejected it.", nameof(rejectedBy));
        var copy = await copies.GetAsync(copyId, ct).ConfigureAwait(false);
        if (copy is not { State: HiveCopyState.InReview })
            return false;
        return await DeprecateAsync(copy, copies, writer, audit, clock.GetUtcNow(), rejectedBy, ct).ConfigureAwait(false);
    }

    private async Task<bool> ReleaseAsync(HiveCopy copy, string actor, DateTimeOffset now, HiveOptions options, CancellationToken ct)
    {
        // The grant, the kind setting or a floor may have changed while the copy was held: a copy never outlives its authority.
        var stillAllowed = options.AllowedKinds.Contains(copy.Kind)
            && await grants.ExistsAsync(copy.FromProject, copy.ToProject, copy.Kind, ct).ConfigureAwait(false);
        if (!stillAllowed)
        {
            await DeprecateAsync(copy, copies, writer, audit, now, actor, ct).ConfigureAwait(false);
            return false;
        }

        // Write first, mark second: a crash between them leaves the copy in review, and the idempotent rewrite is harmless.
        await writer.WriteAsync(copy.Path, HiveDocument.Render(copy), ct).ConfigureAwait(false);
        if (!await copies.MarkActiveAsync(copy.CopyId, now, ct).ConfigureAwait(false))
            return false;
        await audit.AppendAsync(new HiveAuditEvent(now, actor, "hive.copy.released", $"copy={copy.CopyId} path={copy.Path}"), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Marks a copy deprecated; a copy already in the Vault gets its front matter set, never deleted.</summary>
    internal static async Task<bool> DeprecateAsync(
        HiveCopy copy, IHiveCopyStore copies, IHiveVaultWriter writer, IHiveAudit audit, DateTimeOffset now, string actor, CancellationToken ct)
    {
        if (copy.State == HiveCopyState.Active)
            await writer.SetFrontMatterAsync(copy.Path, HiveDocument.StatusKey, HiveDocument.Deprecated, ct).ConfigureAwait(false);
        if (!await copies.MarkDeprecatedAsync(copy.CopyId, now, ct).ConfigureAwait(false))
            return false;
        await audit.AppendAsync(new HiveAuditEvent(now, actor, "hive.copy.deprecated", $"copy={copy.CopyId} path={copy.Path}"), ct).ConfigureAwait(false);
        return true;
    }
}
