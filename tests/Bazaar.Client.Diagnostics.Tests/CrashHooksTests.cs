using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Consent;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Client.Diagnostics.Tests;

public class CrashHooksTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A GitHub token shaped value. Generated for the test; it is not a credential.</summary>
    private const string _fakeToken = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";

    private readonly TempDirectory _root = new();
    private readonly TestClock _clock = new(_start);
    private readonly CrashReportQueue _queue;
    private readonly RingBuffer<CaptureLogEntry> _logs = new(50);
    private ConsentSettings _settings = Samples.Mode(ConsentMode.Ask);

    public CrashHooksTests()
    {
        _queue = new CrashReportQueue(_root.Sub("queue"), CrashQueueLimits.Default, _clock);
    }

    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }

    private CrashHooks Hooks(Func<ConsentSettings>? consent = null) => new(
        new ClientCapture(new Redactor(), new FixedClientInfoProvider()),
        _queue,
        consent ?? (() => _settings),
        _logs,
        Samples.Context(),
        _clock);

    private CrashReport[] Stored() => _queue.List().Select(e => e.Report).ToArray();

    /// <summary>Throws the exception and returns it, so it carries a real stack trace as a handled failure would.</summary>
    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void Consent_off_captures_and_queues_nothing()
    {
        _settings = Samples.Mode(ConsentMode.Off);
        using var hooks = Hooks();

        hooks.Report(new InvalidOperationException("boom"), "BZ-GC-0042");
        hooks.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("crash"), isTerminating: true));
        hooks.FlushPending();

        Assert.Empty(Stored());
        Assert.False(Directory.Exists(_root.Sub("queue")));
    }

    [Fact]
    public void Ask_mode_queues_a_report_with_its_code_and_flagged_frame()
    {
        using var hooks = Hooks();

        hooks.Report(Thrown(new InvalidOperationException("render failed")), "BZ-GC-0042");
        hooks.FlushPending();

        var report = Assert.Single(Stored());
        Assert.Equal("BZ-GC-0042", report.Error.Code);
        Assert.Equal("System.InvalidOperationException", report.Error.Type);
        Assert.Equal("pass", report.Redaction.ResidualScan);
        Assert.Equal(CrashReportSchemas.V1, report.Schema);
        Assert.NotNull(report.FlaggedFrame);
        Assert.Equal("ask", report.Consent.Mode);
        Assert.Equal(Samples.InstallId, report.InstallId);
        Assert.Empty(CrashReportValidation.Validate(report));
    }

    [Fact]
    public void Secrets_in_the_exception_message_never_reach_the_queue()
    {
        using var hooks = Hooks();

        hooks.Report(Thrown(new InvalidOperationException("auth failed with " + _fakeToken)));
        hooks.FlushPending();

        var bytes = File.ReadAllText(Directory.GetFiles(_root.Sub("queue"), "*.json").Single());
        Assert.DoesNotContain("A1b2C3d4E5f6", bytes);
        var report = Assert.Single(Stored());
        Assert.Contains(RedactionMarkers.Pattern, report.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("A1b2C3d4E5f6", report.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inner_exceptions_are_captured_to_a_bounded_depth()
    {
        using var hooks = Hooks();
        var chain = new InvalidOperationException("L0", new InvalidOperationException("L1",
            new InvalidOperationException("L2", new InvalidOperationException("L3",
                new InvalidOperationException("L4", new InvalidOperationException("L5"))))));

        hooks.Report(chain);
        hooks.FlushPending();

        var report = Assert.Single(Stored());
        var inner = report.Logs.Where(entry => entry.Cat == CrashHooks.InnerExceptionCategory).ToArray();
        Assert.Equal(4, inner.Length);
        Assert.Contains("L1", inner[0].Msg);
    }

    [Fact]
    public void Aggregate_exceptions_keep_their_siblings_in_the_chain()
    {
        using var hooks = Hooks();
        var aggregate = new AggregateException(new InvalidOperationException("first"), new ArgumentException("second"));

        hooks.OnUnobservedTaskException(null, new UnobservedTaskExceptionEventArgs(aggregate));
        hooks.FlushPending();

        var report = Assert.Single(Stored());
        Assert.Equal("System.InvalidOperationException", report.Error.Type);
        Assert.Contains(report.Logs, entry => entry.Cat == CrashHooks.InnerExceptionCategory && entry.Msg.Contains("ArgumentException", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_exception_object_is_reported_as_an_exception()
    {
        using var hooks = Hooks();

        hooks.OnUnhandledException(null, new UnhandledExceptionEventArgs("not an exception", isTerminating: false));
        hooks.FlushPending();

        var report = Assert.Single(Stored());
        Assert.Equal("System.InvalidOperationException", report.Error.Type);
    }

    [Fact]
    public void A_malformed_error_code_is_dropped_and_the_report_is_still_queued()
    {
        using var hooks = Hooks();

        hooks.Report(new InvalidOperationException("x"), "not-a-code");
        hooks.FlushPending();

        var report = Assert.Single(Stored());
        Assert.Null(report.Error.Code);
    }

    [Fact]
    public void Unobserved_task_handler_does_not_mark_the_event_observed()
    {
        using var hooks = Hooks();
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("x")));

        hooks.OnUnobservedTaskException(null, args);

        Assert.False(args.Observed);
    }

    [Fact]
    public void Entry_points_never_throw()
    {
        using var hooks = Hooks(() => throw new IOException("settings unreadable"));

        Assert.Null(Record.Exception(() => hooks.Report(new InvalidOperationException("x"))));
        Assert.Null(Record.Exception(() => hooks.OnUnhandledException(null, new UnhandledExceptionEventArgs(new InvalidOperationException("y"), true))));
        Assert.Null(Record.Exception(() => hooks.FlushPending()));
        Assert.Null(Record.Exception(() => hooks.Report(null!)));
        Assert.Empty(Stored());
    }

    [Fact]
    public void Install_is_idempotent_and_dispose_uninstalls()
    {
        var hooks = Hooks();

        hooks.Install();
        hooks.Install();
        Assert.True(hooks.IsInstalled);

        hooks.Dispose();
        Assert.False(hooks.IsInstalled);

        hooks.Dispose();
        hooks.Install();
        Assert.False(hooks.IsInstalled);
    }
}
