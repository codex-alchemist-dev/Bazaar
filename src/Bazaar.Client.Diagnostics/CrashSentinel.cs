using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Consent;
using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Client.Diagnostics;

/// <summary>
/// The crash-on-last-run marker (doc 43 section 7.5). <see cref="Arm"/> writes a <c>crash.sentinel</c> file at start and
/// <see cref="Disarm"/> deletes it on a clean exit. <see cref="CheckPreviousRun"/> runs at the next start: a sentinel that is
/// still there, with no report for its session, becomes one minimal <c>bazaar.crash/1</c> report with reason
/// <c>unclean exit</c>: no error code, no stack, and only the module versions and device information of the run that died.
/// The report goes through the same consent mode and redaction as every other report. The report identifier is the session
/// identifier, so a report that is already queued for the session is found by name. Every method never throws.
/// </summary>
public sealed partial class CrashSentinel
{
    /// <summary>The reason a minimal report carries as its error type and message.</summary>
    public const string UncleanExitReason = "unclean exit";

    /// <summary>The file name the sentinel is written under.</summary>
    public const string FileName = "crash.sentinel";

    private const long _maxSentinelBytes = 64 * 1024;
    private const string _tempSuffix = ".tmp";

    private readonly string _path;
    private readonly CrashReportQueue _queue;
    private readonly IFieldRedactor _redactor;
    private readonly IClientInfoProvider _clientInfo;
    private readonly CrashReportContext _context;
    private readonly Func<ConsentSettings> _consent;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private SentinelState? _armed;
    private bool _checked;

    /// <param name="path">The sentinel file path. Its directory is created on <see cref="Arm"/> when missing.</param>
    /// <param name="queue">The queue that the minimal report is written to, and that is searched for a matching report.</param>
    /// <param name="redactor">The field redactor, the same one the crash capture uses.</param>
    /// <param name="clientInfo">The source of app version, device and module information written at start.</param>
    /// <param name="context">The install, client and device information the host supplies.</param>
    /// <param name="consent">Reads the company's consent settings when a stale sentinel is found.</param>
    /// <param name="time">The clock for report times.</param>
    public CrashSentinel(
        string path,
        CrashReportQueue queue,
        IFieldRedactor redactor,
        IClientInfoProvider clientInfo,
        CrashReportContext context,
        Func<ConsentSettings> consent,
        TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(clientInfo);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(time);

        _path = Path.GetFullPath(path);
        _queue = queue;
        _redactor = redactor;
        _clientInfo = clientInfo;
        _context = context;
        _consent = consent;
        _time = time;
    }

    /// <summary>
    /// Writes the sentinel for this run, atomically. A sentinel left by an earlier run that was not checked yet is checked
    /// first, so it is never overwritten unread. Returns true when the file was written.
    /// </summary>
    public bool Arm()
    {
        CheckPreviousRun();
        try
        {
            lock (_gate)
            {
                var state = new SentinelState(Guid.NewGuid(), ProcessStart(), false, _clientInfo.Current(), _context);
                Write(state);
                _armed = state;
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Deletes the sentinel on a clean exit. Does nothing when it is already gone.</summary>
    public void Disarm()
    {
        try
        {
            lock (_gate)
            {
                _armed = null;
                DeleteIfPresent(_path);
            }
        }
        catch (Exception)
        {
            // A sentinel that cannot be removed gives one minimal report next start; it never fails shutdown.
        }
    }

    /// <summary>
    /// Records that a report of this run is queued, so a later unclean exit does not add a second, minimal report for the
    /// same session. Does nothing when the sentinel is not armed.
    /// </summary>
    public void NoteReport()
    {
        try
        {
            lock (_gate)
            {
                if (_armed is null || _armed.Reported)
                {
                    return;
                }

                _armed = _armed with { Reported = true };
                Write(_armed);
            }
        }
        catch (Exception)
        {
            // Worst case is one extra minimal report after a crash.
        }
    }

    /// <summary>
    /// Looks at the sentinel of the previous run. The file is always removed. A report is built only when consent is not
    /// off, the file is valid, and the queue holds no report for that session. Runs once per instance. Returns true when a
    /// minimal report was queued.
    /// </summary>
    public bool CheckPreviousRun()
    {
        try
        {
            lock (_gate)
            {
                if (_checked)
                {
                    return false;
                }

                _checked = true;
                return CheckCore();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool CheckCore()
    {
        var state = ReadAndRemove();
        if (state is null || state.Reported)
        {
            return false;
        }

        var settings = _consent();
        if (ConsentDecision.Decide(settings, crashReportExists: true) == ConsentAction.SendNothing)
        {
            return false;
        }

        if (_queue.List().Any(entry => entry.ReportId == state.SessionId))
        {
            return false;
        }

        var report = BuildMinimal(state, settings);
        if (report is null)
        {
            return false;
        }

        _queue.Add(report);
        return true;
    }

    private CrashReport? BuildMinimal(SentinelState state, ConsentSettings settings)
    {
        var now = _time.GetUtcNow();

        // A never-thrown exception has no stack, so the capture gives the redacted client information and an empty stack.
        var capture = new ClientCapture(_redactor, new StoredClientInfo(state.Client));
        var captured = capture.Capture(new InvalidOperationException(UncleanExitReason), errorCode: null, [], now);
        var fingerprint = Fingerprint.Compute(new FingerprintInput(null, UncleanExitReason, [], UncleanExitReason));
        var record = captured with
        {
            ExceptionType = UncleanExitReason,
            Message = UncleanExitReason,
            Stack = [],
            FlaggedFrame = null,
            NormalisedTopFrame = null,
            Fingerprint = fingerprint,
        };

        var report = CrashReportBuilder.Build(record, state.Context, settings, state.SessionId, now);
        return CrashReportBuilder.FitToLimits(report);
    }

    /// <summary>Reads the sentinel and deletes the file. A missing, oversized, corrupt or incomplete file gives null.</summary>
    private SentinelState? ReadAndRemove()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        SentinelState? state = null;
        try
        {
            if (new FileInfo(_path).Length <= _maxSentinelBytes)
            {
                var bytes = File.ReadAllBytes(_path);
                state = JsonSerializer.Deserialize(bytes, SentinelJsonContext.Default.SentinelState);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            state = null;
        }

        DeleteIfPresent(_path);
        return state is { Client: not null, Context: not null } && state.SessionId != Guid.Empty ? state : null;
    }

    private void Write(SentinelState state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + _tempSuffix;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SentinelJsonContext.Default.SentinelState);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            DeleteIfPresent(temp);
        }
    }

    private DateTimeOffset ProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        }
        catch (Exception)
        {
            return _time.GetUtcNow();
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class StoredClientInfo(ClientInfo info) : IClientInfoProvider
    {
        public ClientInfo Current() => info;
    }

    /// <summary>What the sentinel file holds. Versions and device information only; no content of the app.</summary>
    internal sealed record SentinelState(Guid SessionId, DateTimeOffset StartedAt, bool Reported, ClientInfo Client, CrashReportContext Context);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(SentinelState))]
    internal sealed partial class SentinelJsonContext : JsonSerializerContext;
}
