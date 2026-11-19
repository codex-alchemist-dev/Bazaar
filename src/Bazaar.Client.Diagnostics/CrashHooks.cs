using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Consent;

namespace Bazaar.Client.Diagnostics;

/// <summary>
/// The in-process crash hooks (doc 43 sections 4.2 and 7.1). <see cref="Install"/> subscribes to the unhandled-exception
/// event of the app domain and to the unobserved-task event. <see cref="Report"/> captures a handled failure that carries an
/// error code. Every entry point never throws, and does nothing while the consent mode is off: no capture, no queue write.
/// A handler does no file I/O. It builds the report, parks it in a bounded in-memory buffer, and schedules
/// <see cref="FlushPending"/> on the thread pool, which writes the queue. Handlers never mark an event as handled.
/// </summary>
public sealed class CrashHooks : IDisposable
{
    /// <summary>The category of the log entries that carry inner exceptions of the chain.</summary>
    public const string InnerExceptionCategory = "Bazaar.Client.Diagnostics.InnerException";

    private const int _maxChainLevels = 4;
    private const int _maxChainEntries = 8;
    private const int _maxPendingReports = 32;
    private const int _maxLogLines = 500;

    private readonly ClientCapture _capture;
    private readonly CrashReportQueue _queue;
    private readonly Func<ConsentSettings> _consent;
    private readonly RingBuffer<CaptureLogEntry>? _logs;
    private readonly CrashReportContext _context;
    private readonly TimeProvider _time;
    private readonly UnhandledExceptionEventHandler _onUnhandled;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _onUnobserved;
    private readonly object _installGate = new();
    private readonly object _pendingGate = new();
    private readonly Queue<CrashReport> _pending = new();
    private readonly object _flushGate = new();
    private bool _installed;
    private bool _disposed;

    /// <param name="capture">The capture, built with the redactor and the client information.</param>
    /// <param name="queue">The queue that finished reports are written to.</param>
    /// <param name="consent">Reads the company's consent settings. Called on every event, so a changed mode applies at once.</param>
    /// <param name="logs">The recent log entries, or null when the host keeps none. The newest <c>log_lines</c> are copied.</param>
    /// <param name="context">The install, client and device information the host supplies.</param>
    /// <param name="time">The clock for capture and report times.</param>
    public CrashHooks(
        ClientCapture capture,
        CrashReportQueue queue,
        Func<ConsentSettings> consent,
        RingBuffer<CaptureLogEntry>? logs,
        CrashReportContext context,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(consent);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(time);

        _capture = capture;
        _queue = queue;
        _consent = consent;
        _logs = logs;
        _context = context;
        _time = time;
        _onUnhandled = OnUnhandledException;
        _onUnobserved = OnUnobservedTaskException;
    }

    /// <summary>True between <see cref="Install"/> and <see cref="Dispose"/>.</summary>
    internal bool IsInstalled
    {
        get
        {
            lock (_installGate)
            {
                return _installed;
            }
        }
    }

    /// <summary>Subscribes the handlers. Calling it again does nothing, and it does nothing after <see cref="Dispose"/>.</summary>
    public void Install()
    {
        lock (_installGate)
        {
            if (_installed || _disposed)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += _onUnhandled;
            TaskScheduler.UnobservedTaskException += _onUnobserved;
            _installed = true;
        }
    }

    /// <summary>Unsubscribes the handlers. Safe to call more than once. A disposed hook cannot be installed again.</summary>
    public void Dispose()
    {
        lock (_installGate)
        {
            if (_installed)
            {
                AppDomain.CurrentDomain.UnhandledException -= _onUnhandled;
                TaskScheduler.UnobservedTaskException -= _onUnobserved;
                _installed = false;
            }

            _disposed = true;
        }
    }

    /// <summary>
    /// Captures a handled failure that the code reports with an error code. A malformed code is dropped and the report is
    /// captured without one. Never throws.
    /// </summary>
    public void Report(Exception exception, string? errorCode = null)
    {
        if (exception is null)
        {
            return;
        }

        Capture(exception, TelemetryErrorCodes.IsWellFormed(errorCode) ? errorCode : null);
    }

    /// <summary>
    /// Writes the parked reports to the queue. Runs at most once at a time, so an explicit call returns only after every
    /// report parked before it is on disk. Reports are dropped, not written, when consent is off at this point. Never throws.
    /// </summary>
    public void FlushPending()
    {
        try
        {
            lock (_flushGate)
            {
                List<CrashReport> batch;
                lock (_pendingGate)
                {
                    batch = [.. _pending];
                    _pending.Clear();
                }

                if (batch.Count == 0 || ConsentDecision.Decide(_consent(), crashReportExists: true) == ConsentAction.SendNothing)
                {
                    return;
                }

                foreach (var report in batch)
                {
                    try
                    {
                        _queue.Add(report);
                    }
                    catch (Exception)
                    {
                        // A report that cannot be written (full or unwritable disk) is lost; the hook never fails the app.
                    }
                }
            }
        }
        catch (Exception)
        {
            // Consent could not be read; nothing is written.
        }
    }

    /// <summary>The unhandled-exception handler. Internal so that tests can drive it without raising a real failure.</summary>
    internal void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception ?? new InvalidOperationException("A non-exception object was thrown.");
        Capture(exception, errorCode: null);
    }

    /// <summary>The unobserved-task handler. It leaves the event observed state as it was. Internal for tests.</summary>
    internal void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Capture(args.Exception, errorCode: null);
    }

    private void Capture(Exception exception, string? errorCode)
    {
        try
        {
            var settings = _consent();
            if (ConsentDecision.Decide(settings, crashReportExists: true) == ConsentAction.SendNothing)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var primary = exception is AggregateException { InnerExceptions.Count: > 0 } aggregate
                ? aggregate.InnerExceptions[0]
                : exception;
            var chain = CollectChain(exception, primary);
            var logs = SnapshotLogs(settings.LogLines, chain, now);

            var record = _capture.Capture(primary, errorCode, logs, now);
            var report = CrashReportBuilder.Build(record, _context, settings, Guid.CreateVersion7(now), now);
            var fitted = CrashReportBuilder.FitToLimits(report);
            if (fitted is null)
            {
                return;
            }

            Park(fitted);
            ThreadPool.QueueUserWorkItem(static state => state.FlushPending(), this, preferLocal: false);
        }
        catch (Exception)
        {
            // A failing hook never fails the app, and never leaves a half-built report behind.
        }
    }

    /// <summary>
    /// The exceptions under the primary one, bounded to four levels and eight entries in all. Sibling exceptions of an
    /// aggregate come first. Their stacks are not captured: the report schema has one stack (doc 43 section 5.1).
    /// </summary>
    private static List<Exception> CollectChain(Exception exception, Exception primary)
    {
        var chain = new List<Exception>();
        if (!ReferenceEquals(exception, primary) && exception is AggregateException aggregate)
        {
            for (var i = 1; i < aggregate.InnerExceptions.Count && chain.Count < _maxChainEntries; i++)
            {
                chain.Add(aggregate.InnerExceptions[i]);
            }
        }

        AppendChildren(primary, chain, depth: 1);
        return chain;
    }

    private static void AppendChildren(Exception parent, List<Exception> chain, int depth)
    {
        if (depth > _maxChainLevels)
        {
            return;
        }

        IEnumerable<Exception> children;
        if (parent is AggregateException aggregate)
        {
            children = aggregate.InnerExceptions;
        }
        else if (parent.InnerException is { } inner)
        {
            children = [inner];
        }
        else
        {
            return;
        }

        foreach (var child in children)
        {
            if (chain.Count >= _maxChainEntries)
            {
                return;
            }

            chain.Add(child);
            AppendChildren(child, chain, depth + 1);
        }
    }

    private List<CaptureLogEntry> SnapshotLogs(int logLines, List<Exception> chain, DateTimeOffset now)
    {
        IReadOnlyList<CaptureLogEntry> recent = _logs?.Snapshot() ?? [];
        var keep = Math.Clamp(logLines, 0, _maxLogLines);
        var lines = recent.Skip(Math.Max(0, recent.Count - keep)).ToList();
        foreach (var inner in chain)
        {
            var type = inner.GetType().FullName ?? inner.GetType().Name;
            lines.Add(new CaptureLogEntry(now, "Error", InnerExceptionCategory, type + ": " + inner.Message));
        }

        return lines;
    }

    private void Park(CrashReport report)
    {
        lock (_pendingGate)
        {
            if (_pending.Count >= _maxPendingReports)
            {
                _pending.Dequeue();
            }

            _pending.Enqueue(report);
        }
    }
}
