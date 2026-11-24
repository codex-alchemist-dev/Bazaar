using System.Text.Json;
using Bazaar.Client.Tui.Dashboard;

namespace Bazaar.Server.OpsPorts;

/// <summary>Appends log lines to the JSON-lines file <see cref="FileOpsLogSource"/> reads. The server's log sink calls this.</summary>
public sealed class OpsLogFileWriter(string path)
{
    public void Append(OpsLogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        JsonLinesFile.Append(path, JsonSerializer.Serialize(
            new LogLineRecord(line.At, line.Level.ToString(), line.Module, line.Message), OpsJsonContext.Default.LogLineRecord));
    }
}

/// <summary>The log tail (doc 58 section 3: tail, filter by level and module) over the JSON-lines file. A missing file is an empty tail.</summary>
public sealed class FileOpsLogSource(string path, int maxReadBytes = OpsPortsOptions.DefaultMaxReadBytes) : IOpsLogSource
{
    public const int MaxLimit = 1000;

    public Task<IReadOnlyList<OpsLogLine>> TailAsync(OpsLogFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ct.ThrowIfCancellationRequested();
        var limit = Math.Clamp(filter.Limit, 1, MaxLimit);
        var module = string.IsNullOrWhiteSpace(filter.Module) ? null : filter.Module.Trim();

        var matches = new List<OpsLogLine>();
        foreach (var raw in JsonLinesFile.ReadTail(path, maxReadBytes))
        {
            var record = JsonLinesFile.TryParse(raw, OpsJsonContext.Default.LogLineRecord);
            if (record is null)
            {
                continue;
            }

            var level = JsonLinesFile.ParseLevel(record.Level);
            if (level < filter.MinLevel || (module is not null && !string.Equals(record.Module, module, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            matches.Add(new OpsLogLine(record.At, level, record.Module ?? string.Empty, record.Message ?? string.Empty));
        }

        // Newest last, as the port says: keep the final `limit` of the file's order.
        IReadOnlyList<OpsLogLine> result = matches.Count > limit ? matches[^limit..] : matches;
        return Task.FromResult(result);
    }
}

/// <summary>Appends health events to the file <see cref="FileOpsEventSource"/> reads. Kodo, the restart path and the backup job call this.</summary>
public sealed class OpsEventFileWriter(string path)
{
    public void Append(OpsEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        JsonLinesFile.Append(path, JsonSerializer.Serialize(
            new EventRecord(evt.Id, evt.At, evt.Kind.ToString(), evt.Summary, evt.Major), OpsJsonContext.Default.EventRecord));
    }
}

/// <summary>Recent health events, newest first, over the JSON-lines file. A missing file is no events.</summary>
public sealed class FileOpsEventSource(string path, int maxReadBytes = OpsPortsOptions.DefaultMaxReadBytes) : IOpsEventSource
{
    public const int MaxLimit = 1000;

    public Task<IReadOnlyList<OpsEvent>> ReadRecentAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var take = Math.Clamp(limit, 1, MaxLimit);
        var events = new List<OpsEvent>();
        foreach (var raw in JsonLinesFile.ReadTail(path, maxReadBytes).Reverse())
        {
            var record = JsonLinesFile.TryParse(raw, OpsJsonContext.Default.EventRecord);
            if (record is null || string.IsNullOrWhiteSpace(record.Id))
            {
                continue;
            }

            events.Add(new OpsEvent(record.Id, record.At, JsonLinesFile.ParseKind(record.Kind), record.Summary ?? string.Empty, record.Major));
            if (events.Count == take)
            {
                break;
            }
        }

        // Events with equal times keep file order newest-first; sorting by time makes the "newest first" promise hold for late appends too.
        IReadOnlyList<OpsEvent> result = events.OrderByDescending(e => e.At).ToList();
        return Task.FromResult(result);
    }
}

/// <summary>
/// The TUI's own crash journal (doc 58 section 3a). A crash is appended before the process dies, so the next start counts it toward
/// the restart delay and shows it as a line on the overview. Never throws on a missing or unreadable file when reading.
/// </summary>
public sealed class FileOpsCrashJournal(string path, int maxReadBytes = OpsPortsOptions.DefaultMaxReadBytes) : IOpsCrashJournal
{
    public const int MaxLimit = 200;

    public Task RecordAsync(OpsCrashNote note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(note);
        ct.ThrowIfCancellationRequested();
        JsonLinesFile.Append(path, JsonSerializer.Serialize(new CrashRecord(note.At, note.Reason), OpsJsonContext.Default.CrashRecord));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OpsCrashNote>> ReadRecentAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var take = Math.Clamp(limit, 1, MaxLimit);
        var notes = new List<OpsCrashNote>();
        foreach (var raw in JsonLinesFile.ReadTail(path, maxReadBytes).Reverse())
        {
            var record = JsonLinesFile.TryParse(raw, OpsJsonContext.Default.CrashRecord);
            if (record is null)
            {
                continue;
            }

            notes.Add(new OpsCrashNote(record.At, record.Reason ?? string.Empty));
            if (notes.Count == take)
            {
                break;
            }
        }

        IReadOnlyList<OpsCrashNote> result = notes;
        return Task.FromResult(result);
    }
}
