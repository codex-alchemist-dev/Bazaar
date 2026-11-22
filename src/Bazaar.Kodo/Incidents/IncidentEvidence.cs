using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Journal;

namespace Bazaar.Kodo.Incidents;

/// <summary>The evidence an incident snapshots (doc 46 section 16.3 item 3), in the order the manifest lists it.</summary>
public enum EvidenceKind
{
    JournalSegment,
    Logs,
    Configuration,
    RuntimeSlotHashes,
    DatabaseSummary,
    ProbeResults,
    FleetList,
}

/// <summary>What happened to one evidence item.</summary>
public enum EvidenceStatus
{
    /// <summary>Stored, with its hash in the manifest.</summary>
    Collected,

    /// <summary>Not stored because a cap was reached.</summary>
    Skipped,

    /// <summary>The source or the store failed.</summary>
    Failed,

    /// <summary>The redaction hook or the secret check refused it.</summary>
    Refused,
}

/// <summary>One manifest entry. <paramref name="Sha256"/> and <paramref name="Size"/> describe the stored bytes (after redaction and truncation).</summary>
/// <param name="Index">Position in the manifest.</param>
/// <param name="Kind">The evidence kind.</param>
/// <param name="Name">A component id, used as the stored file name. Unique inside a manifest.</param>
/// <param name="Size">Stored bytes. 0 unless collected.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the stored bytes, or empty unless collected.</param>
/// <param name="Truncated">True when the item was cut to the per-item cap.</param>
/// <param name="Status">The outcome.</param>
/// <param name="Reason">A fixed code: <c>ok</c> or why it was not collected.</param>
/// <param name="ChainHash">Lower-case hex hash chaining this entry to the one before (the journal's chain function).</param>
public sealed record EvidenceItem(
    int Index, EvidenceKind Kind, string Name, long Size, string Sha256, bool Truncated, EvidenceStatus Status, string Reason, string ChainHash);

/// <summary>
/// The ordered, bounded list of what was snapshotted. Each entry chains to the previous one with <see cref="JournalChain.ComputeHash"/>, and
/// <see cref="Root"/> covers the whole list, so a changed, removed or reordered entry changes the root. The root is what the journal records.
/// </summary>
public sealed record EvidenceManifest(int Version, string IncidentId, DateTimeOffset CreatedAt, IReadOnlyList<EvidenceItem> Items, int OmittedItems, string Root)
{
    /// <summary>The manifest format version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Recomputes every chain hash and the root. Returns the first entry index that does not match, or null (<see cref="Root"/> mismatch alone returns -1).</summary>
    public static int? FirstBroken(EvidenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        byte[] previous = Seed(manifest.IncidentId, manifest.CreatedAt);
        for (int i = 0; i < manifest.Items.Count; i++)
        {
            EvidenceItem item = manifest.Items[i];
            byte[] hash = JournalChain.ComputeHash(previous, Canonical(item));
            if (item.Index != i || !string.Equals(item.ChainHash, Convert.ToHexStringLower(hash), StringComparison.Ordinal))
            {
                return i;
            }

            previous = hash;
        }

        return string.Equals(manifest.Root, RootOf(previous, manifest.OmittedItems), StringComparison.Ordinal) ? null : -1;
    }

    /// <summary>
    /// Checks stored content against the manifest. Returns the names of collected items whose bytes are missing or no longer match their
    /// size and hash. An empty list means the content is intact.
    /// </summary>
    public static IReadOnlyList<string> ContentMismatches(EvidenceManifest manifest, Func<string, byte[]?> read)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(read);
        List<string> bad = [];
        foreach (EvidenceItem item in manifest.Items.Where(i => i.Status == EvidenceStatus.Collected))
        {
            byte[]? bytes = read(item.Name);
            if (bytes is null
                || bytes.LongLength != item.Size
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), item.Sha256, StringComparison.Ordinal))
            {
                bad.Add(item.Name);
            }
        }

        return bad;
    }

    internal static byte[] Seed(string incidentId, DateTimeOffset createdAt) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"kodo-evidence/v1|{incidentId}|{createdAt.ToUniversalTime().UtcTicks}")));

    internal static string RootOf(byte[] last, int omitted) =>
        Convert.ToHexStringLower(JournalChain.ComputeHash(last, Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"omitted|{omitted}"))));

    /// <summary>The canonical bytes of an item, without its chain hash: fixed field order, invariant digits.</summary>
    internal static byte[] Canonical(EvidenceItem item) =>
        Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{item.Index}|{(int)item.Kind}|{item.Name}|{item.Size}|{item.Sha256}|{(item.Truncated ? 1 : 0)}|{(int)item.Status}|{item.Reason}"));
}

/// <summary>One piece of raw evidence from a source, before redaction.</summary>
/// <param name="Name">A component id, such as <c>logs.current</c>.</param>
/// <param name="Content">The bytes.</param>
public sealed record EvidencePart(string Name, byte[] Content);

/// <summary>What a source is asked for.</summary>
/// <param name="IncidentId">The incident.</param>
/// <param name="TriggerWindowStart">The start of the trigger window: the journal segment and logs begin here.</param>
/// <param name="CollectedAt">When the snapshot began.</param>
public sealed record EvidenceRequest(string IncidentId, DateTimeOffset TriggerWindowStart, DateTimeOffset CollectedAt);

/// <summary>
/// One evidence source behind an interface: the journal segment, logs, configuration, slot hashes, database summary (counts and versions
/// only), probe results or the fleet list. The implementation does the I/O and throws on failure; one failure never stops the others.
/// </summary>
public interface IEvidenceSource
{
    /// <summary>The kind this source serves.</summary>
    EvidenceKind Kind { get; }

    /// <summary>Collects the parts. May return several (for example the current and the previous day of logs).</summary>
    ValueTask<IReadOnlyList<EvidencePart>> CollectAsync(EvidenceRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The redaction hook (section 16.3 item 3: no secrets). Every part goes through it before it is hashed or stored. Return the redacted
/// bytes, or null to drop the part. The collector requires a redactor; there is no pass-through default.
/// </summary>
public interface IEvidenceRedactor
{
    /// <summary>Redacts one part.</summary>
    byte[]? Redact(EvidenceKind kind, string name, ReadOnlySpan<byte> content);
}

/// <summary>Where evidence is kept (<c>kodo-evidence</c>, subject to legal hold, doc 33). The implementation throws <see cref="IOException"/> on failure.</summary>
public interface IEvidenceSink
{
    /// <summary>Stores one item's bytes.</summary>
    ValueTask WriteItemAsync(string incidentId, string name, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>Stores the sealed manifest.</summary>
    ValueTask WriteManifestAsync(EvidenceManifest manifest, CancellationToken cancellationToken);
}

/// <summary>Evidence limits (section 16.3 item 3). PROPOSED defaults (section 16.9).</summary>
public sealed record EvidenceOptions
{
    /// <summary>The most manifest entries. Further items are counted in the manifest's omitted number. Default 32.</summary>
    public int MaxItems { get; init; } = 32;

    /// <summary>The most bytes of one item. Longer content is cut and flagged. Default 1 MiB.</summary>
    public int MaxItemBytes { get; init; } = 1024 * 1024;

    /// <summary>The most bytes across all items. An item that would pass it is skipped. Default 16 MiB.</summary>
    public long MaxTotalBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>How long one source may take. Default 30 seconds.</summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How far before the opening time the trigger window starts. Default 1 hour.</summary>
    public TimeSpan TriggerWindow { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Checks every value.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxItems, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxItems, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxItemBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTotalBytes, MaxItemBytes);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SourceTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(TriggerWindow, TimeSpan.Zero);
    }
}

/// <summary>
/// Collects the evidence snapshot (section 16.3 item 3). Sources run one at a time in the manifest order (by kind, then registration), each
/// part is redacted, checked for key material, cut to the cap, stored and hashed, and the manifest is chained and sealed. It does no I/O itself.
/// </summary>
public sealed class IncidentEvidenceCollector
{
    private static readonly byte[] _pemMarker = Encoding.ASCII.GetBytes("-----BEGIN ");
    private static readonly byte[] _privateKeyMarker = Encoding.ASCII.GetBytes("PRIVATE KEY");

    private readonly IReadOnlyList<IEvidenceSource> _sources;
    private readonly IEvidenceRedactor _redactor;
    private readonly IEvidenceSink _sink;
    private readonly EvidenceOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates a collector. The options are validated. A redactor is required.</summary>
    public IncidentEvidenceCollector(
        IEnumerable<IEvidenceSource> sources, IEvidenceRedactor redactor, IEvidenceSink sink, EvidenceOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _sources = sources.OrderBy(source => source.Kind).ToList();
        _redactor = redactor;
        _sink = sink;
        _options = options;
        _time = timeProvider;
    }

    /// <summary>The trigger window start for an incident opened at <paramref name="openedAt"/>.</summary>
    public DateTimeOffset WindowStart(DateTimeOffset openedAt) => openedAt - _options.TriggerWindow;

    /// <summary>Takes the snapshot and seals the manifest. Throws <see cref="IOException"/> when the manifest cannot be stored.</summary>
    public async ValueTask<EvidenceManifest> CollectAsync(string incidentId, DateTimeOffset openedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(incidentId);
        DateTimeOffset now = _time.GetUtcNow();
        EvidenceRequest request = new(incidentId, WindowStart(openedAt), now);

        List<EvidenceItem> items = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        byte[] previous = EvidenceManifest.Seed(incidentId, now);
        long total = 0;
        int omitted = 0;

        foreach (IEvidenceSource source in _sources)
        {
            (IReadOnlyList<EvidencePart>? parts, string? failure) = await RunSourceAsync(source, request, cancellationToken).ConfigureAwait(false);
            if (parts is null)
            {
                Add(source.Kind, $"{source.Kind.ToString().ToLowerInvariant()}.source", 0, string.Empty, false, EvidenceStatus.Failed, failure!);
                continue;
            }

            foreach (EvidencePart part in parts)
            {
                if (items.Count >= _options.MaxItems)
                {
                    omitted++;
                    continue;
                }

                if (part is null || !IncidentText.IsComponent(part.Name) || part.Content is null)
                {
                    Add(source.Kind, "invalid.part", 0, string.Empty, false, EvidenceStatus.Refused, "invalid_part");
                    continue;
                }

                if (!names.Add(part.Name))
                {
                    Add(source.Kind, part.Name, 0, string.Empty, false, EvidenceStatus.Refused, "duplicate_name");
                    continue;
                }

                (EvidenceStatus status, string reason, byte[]? bytes, bool truncated) = Prepare(source.Kind, part, total);
                if (bytes is null)
                {
                    Add(source.Kind, part.Name, 0, string.Empty, false, status, reason);
                    continue;
                }

                try
                {
                    await _sink.WriteItemAsync(incidentId, part.Name, bytes, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    Add(source.Kind, part.Name, 0, string.Empty, false, EvidenceStatus.Failed, "store_error");
                    continue;
                }

                total += bytes.Length;
                Add(source.Kind, part.Name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), truncated, EvidenceStatus.Collected, "ok");
            }
        }

        EvidenceManifest manifest = new(
            EvidenceManifest.CurrentVersion, incidentId, now, items, omitted, EvidenceManifest.RootOf(previous, omitted));
        await _sink.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        return manifest;

        void Add(EvidenceKind kind, string name, long size, string sha, bool truncated, EvidenceStatus status, string reason)
        {
            if (items.Count >= _options.MaxItems)
            {
                omitted++;
                return;
            }

            EvidenceItem draft = new(items.Count, kind, name, size, sha, truncated, status, reason, string.Empty);
            byte[] hash = JournalChain.ComputeHash(previous, EvidenceManifest.Canonical(draft));
            items.Add(draft with { ChainHash = Convert.ToHexStringLower(hash) });
            previous = hash;
        }
    }

    private (EvidenceStatus Status, string Reason, byte[]? Bytes, bool Truncated) Prepare(EvidenceKind kind, EvidencePart part, long total)
    {
        byte[]? redacted;
        try
        {
            redacted = _redactor.Redact(kind, part.Name, part.Content);
        }
        catch (Exception)
        {
            return (EvidenceStatus.Refused, "redactor_error", null, false);
        }

        if (redacted is null)
        {
            return (EvidenceStatus.Refused, "redactor_dropped", null, false);
        }

        if (ContainsKeyMaterial(redacted))
        {
            return (EvidenceStatus.Refused, "secret_detected", null, false);
        }

        bool truncated = redacted.Length > _options.MaxItemBytes;
        byte[] bytes = truncated ? redacted.AsSpan(0, _options.MaxItemBytes).ToArray() : redacted;
        return total + bytes.Length > _options.MaxTotalBytes
            ? (EvidenceStatus.Skipped, "total_cap", null, false)
            : (EvidenceStatus.Collected, "ok", bytes, truncated);
    }

    private async ValueTask<(IReadOnlyList<EvidencePart>? Parts, string? Failure)> RunSourceAsync(
        IEvidenceSource source, EvidenceRequest request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(_options.SourceTimeout, _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            IReadOnlyList<EvidencePart> parts = await source.CollectAsync(request, linked.Token).ConfigureAwait(false);
            return parts is null ? (null, "source_error") : (parts, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "timeout");
        }
        catch (Exception)
        {
            return (null, "source_error");
        }
    }

    private static bool ContainsKeyMaterial(ReadOnlySpan<byte> bytes)
    {
        int at = bytes.IndexOf(_pemMarker);
        while (at >= 0)
        {
            ReadOnlySpan<byte> rest = bytes[(at + _pemMarker.Length)..];
            ReadOnlySpan<byte> line = rest[..Math.Min(rest.Length, 64)];
            if (line.IndexOf(_privateKeyMarker) >= 0)
            {
                return true;
            }

            int next = rest.IndexOf(_pemMarker);
            if (next < 0)
            {
                return false;
            }

            at += _pemMarker.Length + next;
        }

        return false;
    }
}
