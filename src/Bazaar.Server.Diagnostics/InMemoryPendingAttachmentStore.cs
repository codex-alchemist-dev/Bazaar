using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Server.Diagnostics;

/// <summary>Limits for the pending store. Chosen here; doc 43 does not set them.</summary>
public sealed record PendingAttachmentOptions
{
    /// <summary>How long a held half waits for its partner. Chosen here.</summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Entries held at once. Chosen here.</summary>
    public int MaxEntries { get; init; } = 256;

    /// <summary>Screenshot bytes held at once. Chosen here: a few full-size screenshots.</summary>
    public long MaxHeldBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>
/// The in-process <see cref="IPendingAttachmentStore"/>. Entries are bounded by count and by bytes, and expire on the injected
/// clock. It does no I/O, so a restart drops what it holds, as the interface documents.
/// </summary>
public sealed class InMemoryPendingAttachmentStore : IPendingAttachmentStore
{
    private readonly PendingAttachmentOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Held> _held = [];
    private long _bytes;

    /// <summary>Creates the store.</summary>
    public InMemoryPendingAttachmentStore(PendingAttachmentOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (options.Lifetime <= TimeSpan.Zero || options.MaxEntries < 1 || options.MaxHeldBytes < 1)
        {
            throw new ArgumentException("Pending limits must be positive.", nameof(options));
        }

        _options = options;
        _clock = clock;
    }

    /// <inheritdoc />
    public bool HoldUpload(string deviceId, IngestionAttachment upload)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(upload);
        var size = upload.Data.Length;
        lock (_gate)
        {
            Sweep();
            var id = upload.Begin.AttachmentId;
            if (_held.ContainsKey(id) || _held.Count >= _options.MaxEntries || _bytes + size > _options.MaxHeldBytes)
            {
                return false;
            }

            _held[id] = new Held(deviceId, _clock.GetUtcNow() + _options.Lifetime, upload, null, size);
            _bytes += size;
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryTakeUpload(string deviceId, Guid attachmentId, out IngestionAttachment? upload)
    {
        lock (_gate)
        {
            Sweep();
            if (_held.TryGetValue(attachmentId, out var held) && held.Upload is not null && held.DeviceId == deviceId)
            {
                Remove(attachmentId, held);
                upload = held.Upload;
                return true;
            }

            upload = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool HoldReport(string deviceId, PendingReport report)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            Sweep();
            if (_held.ContainsKey(report.AttachmentId) || _held.Count >= _options.MaxEntries)
            {
                return false;
            }

            _held[report.AttachmentId] = new Held(deviceId, _clock.GetUtcNow() + _options.Lifetime, null, report, 0);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryTakeReport(string deviceId, Guid attachmentId, out PendingReport? report)
    {
        lock (_gate)
        {
            Sweep();
            if (_held.TryGetValue(attachmentId, out var held) && held.Report is not null && held.DeviceId == deviceId)
            {
                Remove(attachmentId, held);
                report = held.Report;
                return true;
            }

            report = null;
            return false;
        }
    }

    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var (id, held) in _held.Where(e => e.Value.ExpiresAt <= now).ToList())
        {
            Remove(id, held);
        }
    }

    private void Remove(Guid id, Held held)
    {
        _held.Remove(id);
        _bytes -= held.Bytes;
    }

    private sealed record Held(string DeviceId, DateTimeOffset ExpiresAt, IngestionAttachment? Upload, PendingReport? Report, long Bytes);
}
