using System.Collections.Concurrent;
using Bazaar.Server.GcMetadata;
using Bazaar.Server.Presence.Sources.Abstractions;

namespace Bazaar.Server.Presence.Sources;

/// <summary>
/// <see cref="IGcKindLookup"/> over the GC header (type key and metadata). A <c>council</c> or <c>vote</c> type key or <c>kind</c>
/// metadata slug maps to council or vote; a <c>project</c> type, or any non-private GC with a <c>project</c> metadata slug, maps to a
/// project GC with that slug; everything else is <see cref="TurnGcKind.Other"/>. Answers are cached for the given ttl; a source failure
/// or an unknown GC answers Other and is cached only briefly, so the turn is never held up.
/// </summary>
public sealed class GcKindLookup(IGcHeaderSource? source, TimeProvider time, TimeSpan ttl) : IGcKindLookup
{
    private static readonly TimeSpan _failureTtl = TimeSpan.FromSeconds(5);
    private static readonly HashSet<string> _privateTypes = new(StringComparer.Ordinal) { "dm", "thinking", "vent", "inbox", "ward" };
    private readonly ConcurrentDictionary<Guid, (GcKindInfo Info, DateTimeOffset Expires)> _cache = new();

    /// <inheritdoc />
    public async ValueTask<GcKindInfo> LookupAsync(Guid gcId, CancellationToken ct)
    {
        if (source is null)
            return GcKindInfo.Other;

        var now = time.GetUtcNow();
        if (_cache.TryGetValue(gcId, out var hit) && hit.Expires > now)
            return hit.Info;

        try
        {
            var header = await source.FindAsync(gcId, ct).ConfigureAwait(false);
            var info = header is null ? GcKindInfo.Other : Map(header);
            _cache[gcId] = (info, now + (header is null ? _failureTtl : ttl));
            return info;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _cache[gcId] = (GcKindInfo.Other, now + _failureTtl);
            return GcKindInfo.Other;
        }
    }

    /// <summary>Maps a GC header to its presence kind.</summary>
    public static GcKindInfo Map(GcHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        header.Metadata.TryGetValue(GcMetadataKeys.Kind, out var kind);
        header.Metadata.TryGetValue(GcMetadataKeys.Project, out var project);
        project = string.IsNullOrWhiteSpace(project) ? null : project;

        if (header.TypeKey == "council" || kind == "council")
            return new GcKindInfo(TurnGcKind.Council);
        if (header.TypeKey == "vote" || kind == "vote")
            return new GcKindInfo(TurnGcKind.Vote);
        if (header.TypeKey == "project" || (project is not null && !_privateTypes.Contains(header.TypeKey)))
            return new GcKindInfo(TurnGcKind.Project, project);
        return GcKindInfo.Other;
    }
}
