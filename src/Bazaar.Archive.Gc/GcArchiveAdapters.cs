using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Archive.Gc;

/// <summary>What the GC owner supplies that the log reader does not: message text, the thinking flag and the archive switch.</summary>
public interface IGcArchiveBridge
{
    /// <summary>Whether the GC is a Thinking GC. Its text is never archived (doc 25 floors).</summary>
    Task<bool> IsThinkingGcAsync(Guid gcId, CancellationToken ct);

    /// <summary>The opened text of a message version, or null when erased. Null is archived as an empty body.</summary>
    Task<string?> GetMessageTextAsync(Guid gcId, Guid messageId, int version, CancellationToken ct);

    /// <summary>Sets <c>archived_at</c> through a system event (doc 01). Never deletes anything.</summary>
    Task MarkGcArchivedAsync(Guid gcId, string rootHash, CancellationToken ct);
}

/// <summary>Lists the GCs of a project, in a stable order.</summary>
public interface IProjectGcLister
{
    Task<IReadOnlyList<Guid>> ListGcIdsAsync(string projectId, CancellationToken ct);
}

/// <summary>Archive unit ids for GCs and projects.</summary>
public static class GcArchiveUnits
{
    public static ArchiveUnit ForGc(Guid gcId) => new(ArchiveUnitKind.GroupChat, gcId.ToString("D"));

    public static ArchiveUnit ForProject(string projectId) => new(ArchiveUnitKind.Project, projectId);
}

/// <summary>Reads one GC's events as archive records, as the owner. Record id is <c>seq-&lt;n&gt;</c>.</summary>
public sealed class GcArchiveSource(IGcLogReader reader, IGcArchiveBridge bridge, GcPrincipal owner, int pageSize = 1000) : IArchiveSource
{
    public static string RecordId(long seq) => "seq-" + seq;

    public async IAsyncEnumerable<ArchiveRecord> ReadAsync(
        ArchiveUnit unit, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var gcId = ParseGc(unit);
        if (await bridge.IsThinkingGcAsync(gcId, ct))
        {
            throw new InvalidOperationException("A Thinking GC is never archived.");
        }

        long after = 0;
        while (true)
        {
            var count = 0;
            await foreach (var e in reader.ReadAsync(gcId, after, pageSize, owner, ct))
            {
                count++;
                after = e.Seq;
                var body = e.MessageId is { } id && e.Version is { } v ? await bridge.GetMessageTextAsync(gcId, id, v, ct) ?? string.Empty : string.Empty;
                yield return new ArchiveRecord(RecordId(e.Seq), e.Kind, e.CreatedAt, body);
            }

            if (count < pageSize)
            {
                yield break;
            }
        }
    }

    public Task MarkArchivedAsync(ArchiveUnit unit, string rootHash, CancellationToken ct) =>
        bridge.MarkGcArchivedAsync(ParseGc(unit), rootHash, ct);

    internal static Guid ParseGc(ArchiveUnit unit) =>
        unit.Kind == ArchiveUnitKind.GroupChat && Guid.TryParseExact(unit.Id, "D", out var g)
            ? g
            : throw new ArgumentException("Not a GC unit.", nameof(unit));
}

/// <summary>
/// Reads every GC of a project in order. Record ids are <c>&lt;gc-id&gt;/seq-&lt;n&gt;</c>. Marking archives each GC through the bridge.
/// </summary>
public sealed class ProjectArchiveSource(IProjectGcLister lister, GcArchiveSource gcSource) : IArchiveSource
{
    public async IAsyncEnumerable<ArchiveRecord> ReadAsync(
        ArchiveUnit unit, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var gcId in await lister.ListGcIdsAsync(unit.Id, ct))
        {
            await foreach (var r in gcSource.ReadAsync(GcArchiveUnits.ForGc(gcId), ct))
            {
                yield return r with { Id = gcId.ToString("D") + "/" + r.Id };
            }
        }
    }

    public async Task MarkArchivedAsync(ArchiveUnit unit, string rootHash, CancellationToken ct)
    {
        foreach (var gcId in await lister.ListGcIdsAsync(unit.Id, ct))
        {
            await gcSource.MarkArchivedAsync(GcArchiveUnits.ForGc(gcId), rootHash, ct);
        }
    }
}
