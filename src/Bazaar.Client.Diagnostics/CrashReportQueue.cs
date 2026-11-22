using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Bazaar.Contracts.Telemetry;

namespace Bazaar.Client.Diagnostics;

/// <summary>
/// The limits of the local report queue. The defaults are this module's choice; doc 43 section 7.3 sets only the
/// age (<c>telemetry.crash.local_queue_days</c>), which the host passes in as <see cref="MaxAge"/>.
/// </summary>
/// <param name="MaxCount">The most reports kept. Oldest are dropped first.</param>
/// <param name="MaxBytes">The most bytes kept across all reports. Oldest are dropped first.</param>
/// <param name="MaxAge">Reports older than this (by their creation time) are dropped.</param>
public sealed record CrashQueueLimits(int MaxCount, long MaxBytes, TimeSpan MaxAge)
{
    /// <summary>200 reports, 32 MiB, 14 days.</summary>
    public static CrashQueueLimits Default { get; } = new(200, 32L * 1024 * 1024, TimeSpan.FromDays(14));
}

/// <summary>One report read from the queue.</summary>
/// <param name="ReportId">The report identifier, which is also the file name.</param>
/// <param name="Report">The parsed report.</param>
/// <param name="SizeBytes">The file size on disk.</param>
/// <param name="Quarantined">True for a report the server refused. It is kept for the owner and never uploaded again.</param>
public sealed record QueuedReport(Guid ReportId, CrashReport Report, long SizeBytes, bool Quarantined);

/// <summary>
/// The on-disk queue of finished, redacted reports (doc 43 sections 4.2 and 7.4). Each report is one file named by its
/// report identifier. Writes are atomic: the bytes go to a temporary file that is then renamed over the target.
/// File names are built only from <see cref="Guid"/> values, so no input can point outside the queue directory.
/// Corrupt files of the queue's own name pattern are deleted when read; files that are not the queue's are left alone.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "CrashReportQueue is the name fixed by the crash pipeline brief (doc 43 section 1, 'report queue').")]
public sealed class CrashReportQueue
{
    private const string _reportExtension = ".json";
    private const string _quarantineExtension = ".refused";
    private const string _tempExtension = ".tmp";

    private readonly string _directory;
    private readonly CrashQueueLimits _limits;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    /// <param name="directory">The queue directory. Created on the first write.</param>
    /// <param name="limits">The count, byte and age limits.</param>
    /// <param name="time">The clock used for the age limit.</param>
    public CrashReportQueue(string directory, CrashQueueLimits limits, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxBytes, 1L);

        _directory = Path.GetFullPath(directory);
        _limits = limits;
        _time = time;
    }

    /// <summary>
    /// Writes one report atomically, then enforces the limits. Writing a report with an identifier that is already
    /// queued replaces it.
    /// </summary>
    public void Add(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, CrashContractsJsonContext.Default.CrashReport);
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var temp = PathFor(report.ReportId, _tempExtension);
            try
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, PathFor(report.ReportId, _reportExtension), overwrite: true);
            }
            finally
            {
                DeleteIfPresent(temp);
            }

            Enforce();
        }
    }

    /// <summary>The reports waiting to be sent, oldest first. Corrupt files are deleted and skipped; quarantined reports are not listed.</summary>
    public IReadOnlyList<QueuedReport> List()
    {
        lock (_gate)
        {
            return ReadEntries(includeQuarantined: false);
        }
    }

    /// <summary>Deletes a queued report, whether waiting or quarantined. Returns true when a file was removed.</summary>
    public bool Remove(Guid reportId)
    {
        lock (_gate)
        {
            var removed = DeleteIfPresent(PathFor(reportId, _reportExtension));
            removed |= DeleteIfPresent(PathFor(reportId, _quarantineExtension));
            return removed;
        }
    }

    /// <summary>
    /// Moves a waiting report to quarantine after the server refused it. It stays on disk for the owner and is never
    /// uploaded again. Returns false when no waiting report has that identifier.
    /// </summary>
    public bool Quarantine(Guid reportId)
    {
        lock (_gate)
        {
            var source = PathFor(reportId, _reportExtension);
            if (!File.Exists(source))
            {
                return false;
            }

            File.Move(source, PathFor(reportId, _quarantineExtension), overwrite: true);
            return true;
        }
    }

    private void Enforce()
    {
        foreach (var temp in Directory.GetFiles(_directory, "*" + _tempExtension))
        {
            DeleteIfPresent(temp);
        }

        var cutoff = _time.GetUtcNow() - _limits.MaxAge;
        var kept = new List<QueuedReport>();
        foreach (var entry in ReadEntries(includeQuarantined: true))
        {
            if (entry.Report.CreatedAt < cutoff)
            {
                DeleteIfPresent(PathFor(entry.ReportId, ExtensionOf(entry)));
            }
            else
            {
                kept.Add(entry);
            }
        }

        var count = kept.Count;
        var bytes = kept.Sum(entry => entry.SizeBytes);
        foreach (var oldest in kept)
        {
            if (count <= _limits.MaxCount && bytes <= _limits.MaxBytes)
            {
                break;
            }

            DeleteIfPresent(PathFor(oldest.ReportId, ExtensionOf(oldest)));
            count--;
            bytes -= oldest.SizeBytes;
        }
    }

    private List<QueuedReport> ReadEntries(bool includeQuarantined)
    {
        var result = new List<QueuedReport>();
        if (!Directory.Exists(_directory))
        {
            return result;
        }

        foreach (var path in Directory.GetFiles(_directory))
        {
            if (!TryParseName(Path.GetFileName(path), out var reportId, out var quarantined))
            {
                continue;
            }

            if (quarantined && !includeQuarantined)
            {
                continue;
            }

            var bytes = File.ReadAllBytes(path);
            CrashReport? report;
            try
            {
                report = JsonSerializer.Deserialize(bytes, CrashContractsJsonContext.Default.CrashReport);
            }
            catch (JsonException)
            {
                report = null;
            }

            if (report is null || report.ReportId != reportId)
            {
                DeleteIfPresent(path);
                continue;
            }

            result.Add(new QueuedReport(reportId, report, bytes.LongLength, quarantined));
        }

        result.Sort((a, b) =>
        {
            var byTime = a.Report.CreatedAt.CompareTo(b.Report.CreatedAt);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.ReportId.ToString("D"), b.ReportId.ToString("D"));
        });
        return result;
    }

    /// <summary>Accepts only the canonical name of a report or a quarantined report: a lower-case GUID and one of our extensions.</summary>
    private static bool TryParseName(string fileName, out Guid reportId, out bool quarantined)
    {
        reportId = default;
        quarantined = false;

        string stem;
        if (fileName.EndsWith(_reportExtension, StringComparison.Ordinal))
        {
            stem = fileName[..^_reportExtension.Length];
        }
        else if (fileName.EndsWith(_quarantineExtension, StringComparison.Ordinal))
        {
            stem = fileName[..^_quarantineExtension.Length];
            quarantined = true;
        }
        else
        {
            return false;
        }

        return Guid.TryParseExact(stem, "D", out reportId) && string.Equals(stem, reportId.ToString("D"), StringComparison.Ordinal);
    }

    private static string ExtensionOf(QueuedReport entry) => entry.Quarantined ? _quarantineExtension : _reportExtension;

    private static bool DeleteIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private string PathFor(Guid reportId, string extension) => Path.Combine(_directory, reportId.ToString("D") + extension);
}
