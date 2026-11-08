using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Calendar;

/// <summary>The Company Calendar's writer (docs/design/24 section 4). The only code that changes <c>calendar_event</c>.</summary>
public interface ICalendarService
{
    /// <summary>Creates an event. A vacation starts proposed unless the actor may confirm it.</summary>
    Task<CalendarResult> CreateAsync(CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Writes a new version of an event if <paramref name="expectedVersion"/> still matches. The status is kept.</summary>
    Task<CalendarResult> UpdateAsync(Guid id, long expectedVersion, CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Confirms a proposed vacation under <c>calendar.vacation_confirmer</c>.</summary>
    Task<CalendarResult> ConfirmAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Cancels an event. Cancel is a status change; the row stays.</summary>
    Task<CalendarResult> CancelAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default);

    /// <summary>Reads one event. The title stays sealed; see <see cref="CalendarService.OpenTitle"/>.</summary>
    Task<CalendarEventRecord?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lists events overlapping [<paramref name="from"/>, <paramref name="until"/>), including cancelled rows.</summary>
    Task<IReadOnlyList<CalendarEventRecord>> ListOverlappingAsync(DateTimeOffset from, DateTimeOffset until, Guid? agentId, CancellationToken ct = default);
}

/// <summary>
/// Calendar writes (docs/design/24 section 4). Each change runs in one transaction that locks the row, checks the actor and the
/// expected version, and writes the new version. After the commit it appends one GC line through <see cref="IGcLogWriter"/>.
/// <para>
/// GC seam: the GC writer opens its own connection and transaction, so the line cannot share the calendar transaction. The
/// line's client message id is <c>calendar:{id}:{version}:{change}</c>, so <see cref="AnnounceAsync"/> can be run again
/// after a failed append without writing a second line. If the append throws, the change stays committed and the exception
/// reaches the caller.
/// </para>
/// <para>
/// The GC line names the change, the kind, the times and the version. It never carries the title, so no personal text leaves
/// its sealed column.
/// </para>
/// </summary>
public sealed class CalendarService(
    NpgsqlDataSource dataSource,
    IGcBodyCodec bodies,
    IGcLogWriter gcWriter,
    CalendarPolicy? policy = null) : ICalendarService
{
    private readonly CalendarRepository _events = new(dataSource);
    private readonly CalendarPolicy _policy = policy ?? new CalendarPolicy();

    /// <inheritdoc />
    public async Task<CalendarResult> CreateAsync(CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        CheckActor(actor);

        var invalid = CalendarRules.ValidateDraft(draft);
        if (invalid is not null)
            return new CalendarResult(CalendarOutcome.Invalid, Reason: invalid);
        if (!CalendarRules.MayCreate(actor, draft.Kind, draft.AgentId, _policy))
            return new CalendarResult(CalendarOutcome.Refused, Reason: "The actor may not create this kind of event.");

        var record = BuildRecord(Guid.CreateVersion7(), draft, version: 1, CalendarRules.InitialStatus(actor, draft.Kind, _policy));

        await using (var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            await CalendarRepository.InsertAsync(conn, tx, record, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await AnnounceAsync(record, CalendarChangeKind.Created, actor, noticeGcId, ct).ConfigureAwait(false);
        return new CalendarResult(CalendarOutcome.Applied, record);
    }

    /// <inheritdoc />
    public async Task<CalendarResult> UpdateAsync(
        Guid id, long expectedVersion, CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        CheckActor(actor);

        var invalid = CalendarRules.ValidateDraft(draft);
        if (invalid is not null)
            return new CalendarResult(CalendarOutcome.Invalid, Reason: invalid);

        CalendarEventRecord next;
        await using (var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var seen = await CalendarRepository.GetForUpdateAsync(conn, tx, id, ct).ConfigureAwait(false);
            if (seen is null)
                return new CalendarResult(CalendarOutcome.NotFound, Reason: "No calendar event has that id.");
            if (!CalendarRules.MayModify(actor, seen.Kind, seen.AgentId, seen.Status, _policy)
                || !CalendarRules.MayCreate(actor, draft.Kind, draft.AgentId, _policy))
                return new CalendarResult(CalendarOutcome.Refused, seen, "The actor may not change this event.");
            if (seen.Version != expectedVersion)
                return new CalendarResult(CalendarOutcome.Conflict, seen, $"Stored version is {seen.Version}.");
            if (seen.Status == CalendarStatus.Cancelled)
                return new CalendarResult(CalendarOutcome.Invalid, seen, "A cancelled event cannot change.");
            if (draft.Kind != seen.Kind)
                return new CalendarResult(CalendarOutcome.Invalid, seen, "An event keeps its kind.");

            next = BuildRecord(id, draft, seen.Version + 1, seen.Status);
            if (!await CalendarRepository.UpdateAsync(conn, tx, next, expectedVersion, ct).ConfigureAwait(false))
                return new CalendarResult(CalendarOutcome.Conflict, seen, "Stored version changed during the update.");
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await AnnounceAsync(next, CalendarChangeKind.Updated, actor, noticeGcId, ct).ConfigureAwait(false);
        return new CalendarResult(CalendarOutcome.Applied, next);
    }

    /// <inheritdoc />
    public Task<CalendarResult> ConfirmAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) =>
        ChangeStatusAsync(id, expectedVersion, actor, noticeGcId, CalendarChangeKind.Confirmed, ct);

    /// <inheritdoc />
    public Task<CalendarResult> CancelAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) =>
        ChangeStatusAsync(id, expectedVersion, actor, noticeGcId, CalendarChangeKind.Cancelled, ct);

    /// <inheritdoc />
    public Task<CalendarEventRecord?> GetAsync(Guid id, CancellationToken ct = default) => _events.GetAsync(id, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<CalendarEventRecord>> ListOverlappingAsync(DateTimeOffset from, DateTimeOffset until, Guid? agentId, CancellationToken ct = default) =>
        _events.ListOverlappingAsync(from, until, agentId, ct);

    /// <summary>Opens the sealed title of a stored event with the body codec. Throws when the codec does not hold its key.</summary>
    public string OpenTitle(CalendarEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return bodies.Open(record.TitleCiphertext, record.TitleKeyId, record.TitleNonce ?? ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// Appends the GC line that announces <paramref name="record"/>'s current version. Idempotent per (event, version, change), so
    /// it can be run again after a failed append.
    /// </summary>
    public async Task AnnounceAsync(CalendarEventRecord record, CalendarChangeKind change, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        CheckActor(actor);

        var payload = new JsonObject
        {
            ["change"] = CalendarRules.ToText(change),
            ["event_id"] = record.Id.ToString(),
            ["kind"] = CalendarRules.ToSql(record.Kind),
            ["status"] = CalendarRules.ToSql(record.Status),
            ["starts_at"] = JsonValue.Create(record.StartsAt),
            ["ends_at"] = JsonValue.Create(record.EndsAt),
            ["all_day"] = record.AllDay,
            ["tz"] = record.Tz,
            ["agent_id"] = record.AgentId?.ToString(),
            ["project_id"] = record.ProjectId?.ToString(),
            ["affects_availability"] = record.AffectsAvailability,
            ["version"] = record.Version,
        };

        var request = new AppendRequest(
            noticeGcId,
            GcEventKinds.System,
            actor.Kind,
            actor.PrincipalId,
            CalendarRules.SourceTrustFor(actor.Kind),
            $"calendar:{record.Id:D}:{record.Version}:{CalendarRules.ToText(change)}")
        {
            PayloadJson = payload.ToJsonString(),
        };
        await gcWriter.AppendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<CalendarResult> ChangeStatusAsync(
        Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CalendarChangeKind change, CancellationToken ct)
    {
        CheckActor(actor);

        CalendarEventRecord next;
        await using (var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            var seen = await CalendarRepository.GetForUpdateAsync(conn, tx, id, ct).ConfigureAwait(false);
            if (seen is null)
                return new CalendarResult(CalendarOutcome.NotFound, Reason: "No calendar event has that id.");

            var allowed = change == CalendarChangeKind.Confirmed
                ? CalendarRules.MayConfirm(actor, seen.Kind, _policy)
                : CalendarRules.MayCancel(actor, seen.Kind, seen.AgentId, _policy);
            if (!allowed)
                return new CalendarResult(CalendarOutcome.Refused, seen, "The actor may not make this change.");
            if (seen.Version != expectedVersion)
                return new CalendarResult(CalendarOutcome.Conflict, seen, $"Stored version is {seen.Version}.");

            if (change == CalendarChangeKind.Confirmed && seen.Status != CalendarStatus.Proposed)
                return new CalendarResult(CalendarOutcome.Invalid, seen, "Only a proposed event can be confirmed.");
            if (change == CalendarChangeKind.Cancelled && seen.Status == CalendarStatus.Cancelled)
                return new CalendarResult(CalendarOutcome.Invalid, seen, "The event is already cancelled.");

            var status = change == CalendarChangeKind.Confirmed ? CalendarStatus.Confirmed : CalendarStatus.Cancelled;
            next = seen with { Status = status, Version = seen.Version + 1 };
            if (!await CalendarRepository.UpdateAsync(conn, tx, next, expectedVersion, ct).ConfigureAwait(false))
                return new CalendarResult(CalendarOutcome.Conflict, seen, "Stored version changed during the update.");
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        await AnnounceAsync(next, change, actor, noticeGcId, ct).ConfigureAwait(false);
        return new CalendarResult(CalendarOutcome.Applied, next);
    }

    /// <summary>Rejects an actor kind the GC line cannot carry. Runs before any write, so a bad actor never commits a change.</summary>
    private static void CheckActor(CalendarActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        _ = CalendarRules.SourceTrustFor(actor.Kind);
    }

    private CalendarEventRecord BuildRecord(Guid id, CalendarDraft draft, long version, CalendarStatus status)
    {
        var sealedTitle = bodies.Seal(draft.Title.Trim());
        return new CalendarEventRecord
        {
            Id = id,
            Kind = draft.Kind,
            TitleKeyId = sealedTitle.KeyId,
            TitleCiphertext = sealedTitle.BodyCiphertext.ToArray(),
            TitleNonce = sealedTitle.Nonce.IsEmpty ? null : sealedTitle.Nonce.ToArray(),
            StartsAt = draft.StartsAt,
            EndsAt = draft.EndsAt,
            AllDay = draft.AllDay,
            Tz = draft.Tz.Trim(),
            AgentId = draft.AgentId,
            ProjectId = draft.ProjectId,
            AffectsAvailability = CalendarRules.AffectsAvailability(draft.Kind, _policy),
            Status = status,
            SourceGcId = draft.SourceGcId,
            Version = version,
        };
    }
}
