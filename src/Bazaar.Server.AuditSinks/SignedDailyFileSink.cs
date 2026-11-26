using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.AuditSinks;

/// <summary>Settings for <see cref="SignedDailyFileSink"/>.</summary>
/// <param name="Directory">Where the daily files go. An operator setting with no default: the sink is off until it is set.</param>
public sealed record SignedDailyFileOptions(string Directory);

/// <summary>
/// Signed daily file export (doc 48 section 5.4, doc 15). One NDJSON file per UTC day, <c>audit-yyyy-MM-dd.ndjson</c>,
/// holding that day's rows and the checkpoints created that day. Beside it, <c>.sig.json</c> carries the signature over
/// the exact file bytes and the key id. Each write rebuilds the day from the existing file plus the new lines (deduped by
/// sequence), signs it, and replaces both files by temp file and rename, so a reader sees the old or the new file and a
/// crash between the two renames only leaves a signature that the next delivery rewrites.
/// </summary>
public sealed class SignedDailyFileSink : IAuditSink, IDisposable
{
    private readonly SignedDailyFileOptions _options;
    private readonly IAuditSinkSigner _signer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SignedDailyFileSink(SignedDailyFileOptions options, IAuditSinkSigner signer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(signer);
        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            throw new ArgumentException("The daily export directory is not set.", nameof(options));
        }

        _options = options;
        _signer = signer;
    }

    public string Name => "daily-file";

    public void Dispose() => _gate.Dispose();

    public Task DeliverRowsAsync(IReadOnlyList<AuditSinkRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return WriteAsync(rows.Select(r => (Day(r.Ts), AuditSinkJson.Row(r))), ct);
    }

    public Task DeliverCheckpointsAsync(IReadOnlyList<AuditSinkCheckpoint> checkpoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        return WriteAsync(checkpoints.Select(c => (Day(c.CreatedAt), AuditSinkJson.Checkpoint(c))), ct);
    }

    /// <summary>Path of the data file for a UTC day.</summary>
    public string DataPath(DateOnly day) => Path.Combine(_options.Directory, "audit-" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".ndjson");

    /// <summary>Path of the signature file for a UTC day.</summary>
    public string SignaturePath(DateOnly day) => DataPath(day) + ".sig.json";

    private static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);

    private async Task WriteAsync(IEnumerable<(DateOnly Day, string Line)> items, CancellationToken ct)
    {
        var byDay = items.GroupBy(i => i.Day).ToList();
        if (byDay.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_options.Directory);
            foreach (var group in byDay)
            {
                await WriteDayAsync(group.Key, group.Select(g => g.Line), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WriteDayAsync(DateOnly day, IEnumerable<string> newLines, CancellationToken ct)
    {
        var path = DataPath(day);
        var rows = new SortedDictionary<long, string>();
        var checkpoints = new SortedDictionary<long, string>();

        if (File.Exists(path))
        {
            foreach (var line in await File.ReadAllLinesAsync(path, Encoding.UTF8, ct).ConfigureAwait(false))
            {
                if (line.Length > 0)
                {
                    Add(line);
                }
            }
        }

        foreach (var line in newLines)
        {
            Add(line);
        }

        var text = new StringBuilder();
        foreach (var line in rows.Values.Concat(checkpoints.Values))
        {
            text.Append(line).Append('\n');
        }

        var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
        var signature = _signer.Sign(bytes);
        var sig = AuditSinkJson.Object(
        [
            new("file", Path.GetFileName(path)),
            new("keyId", _signer.KeyId),
            new("sha256", Convert.ToHexStringLower(SHA256.HashData(bytes))),
            new("signature", Convert.ToBase64String(signature)),
        ]);

        await AtomicWriteAsync(path, bytes, ct).ConfigureAwait(false);
        await AtomicWriteAsync(SignaturePath(day), Encoding.UTF8.GetBytes(sig), ct).ConfigureAwait(false);

        void Add(string line)
        {
            var (type, order) = AuditSinkJson.Classify(line);
            // Last write wins on a repeated sequence; for a true re-delivery the line is identical.
            (type == "row" ? rows : checkpoints)[order] = line;
        }
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}
