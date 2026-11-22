using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Consent;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Client.Diagnostics.Tests;

public class CrashSentinelTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _root = new();
    private readonly TestClock _clock = new(_start);
    private readonly CrashReportQueue _queue;
    private ConsentSettings _settings = Samples.Mode(ConsentMode.Ask);

    public CrashSentinelTests()
    {
        _queue = new CrashReportQueue(_root.Sub("queue"), CrashQueueLimits.Default, _clock);
    }

    private string SentinelPath => _root.Sub(CrashSentinel.FileName);

    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }

    private CrashSentinel Sentinel() => new(
        SentinelPath,
        _queue,
        new Redactor(),
        new FixedClientInfoProvider(),
        Samples.Context(),
        () => _settings,
        _clock);

    [Fact]
    public void Armed_and_disarmed_leaves_no_report()
    {
        var first = Sentinel();
        Assert.True(first.Arm());
        Assert.True(File.Exists(SentinelPath));
        first.Disarm();
        Assert.False(File.Exists(SentinelPath));

        var next = Sentinel();
        Assert.False(next.CheckPreviousRun());
        Assert.Empty(_queue.List());
    }

    [Fact]
    public void Armed_and_never_disarmed_gives_one_minimal_report_next_launch()
    {
        Sentinel().Arm();

        var next = Sentinel();
        Assert.True(next.CheckPreviousRun());

        var report = Assert.Single(_queue.List()).Report;
        Assert.Equal(CrashReportSchemas.V1, report.Schema);
        Assert.Null(report.Error.Code);
        Assert.Equal(CrashSentinel.UncleanExitReason, report.Error.Type);
        Assert.Equal(CrashSentinel.UncleanExitReason, report.Error.Message);
        Assert.Equal("0.1.0+a1b2c3d", report.Client.Version);
        Assert.Contains(report.Modules, m => m.Id == "Bazaar.Client.Diagnostics" && m.Version == "0.1.0");
        Assert.Equal("linux", report.Device.OsFamily);
        Assert.Equal("ask", report.Consent.Mode);
        Assert.Equal(CrashReportBuilder.ResidualPass, report.Redaction.ResidualScan);
        Assert.Empty(CrashReportValidation.Validate(report));
        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Minimal_report_has_no_stack_and_no_logs()
    {
        Sentinel().Arm();
        Sentinel().CheckPreviousRun();

        var report = Assert.Single(_queue.List()).Report;
        Assert.Empty(report.Stack);
        Assert.Null(report.FlaggedFrame);
        Assert.Empty(report.Logs);
    }

    [Fact]
    public void Second_launch_gives_no_second_report()
    {
        Sentinel().Arm();
        Sentinel().CheckPreviousRun();
        Assert.Single(_queue.List());

        Assert.False(Sentinel().CheckPreviousRun());
        Assert.Single(_queue.List());
    }

    [Fact]
    public void Consent_off_writes_nothing_and_removes_the_file()
    {
        Sentinel().Arm();
        _settings = Samples.Mode(ConsentMode.Off);

        Assert.False(Sentinel().CheckPreviousRun());

        Assert.Empty(_queue.List());
        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Corrupt_sentinel_is_deleted_without_a_report()
    {
        File.WriteAllText(SentinelPath, "{ not json");

        Assert.False(Sentinel().CheckPreviousRun());

        Assert.Empty(_queue.List());
        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Oversized_sentinel_is_deleted_without_a_report()
    {
        File.WriteAllBytes(SentinelPath, new byte[128 * 1024]);

        Assert.False(Sentinel().CheckPreviousRun());

        Assert.Empty(_queue.List());
        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Sentinel_with_a_missing_session_is_deleted_without_a_report()
    {
        File.WriteAllText(SentinelPath, "{\"sessionId\":\"00000000-0000-0000-0000-000000000000\"}");

        Assert.False(Sentinel().CheckPreviousRun());

        Assert.Empty(_queue.List());
        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Session_that_already_has_a_report_in_the_queue_gives_no_second_one()
    {
        Sentinel().Arm();
        var state = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllBytes(SentinelPath), CrashSentinel.SentinelJsonContext.Default.SentinelState);
        Assert.NotNull(state);
        _queue.Add(Samples.Report(state.SessionId, _start));

        Assert.False(Sentinel().CheckPreviousRun());

        Assert.Single(_queue.List());
    }

    [Fact]
    public void Run_that_queued_its_own_report_gives_no_minimal_one()
    {
        var sentinel = Sentinel();
        sentinel.Arm();
        sentinel.NoteReport();

        Assert.False(Sentinel().CheckPreviousRun());

        Assert.Empty(_queue.List());
        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Arm_checks_a_stale_sentinel_before_overwriting_it()
    {
        Sentinel().Arm();

        var next = Sentinel();
        Assert.True(next.Arm());

        Assert.Single(_queue.List());
        Assert.True(File.Exists(SentinelPath));
    }

    [Fact]
    public void Sentinel_file_holds_versions_and_device_only()
    {
        Sentinel().Arm();

        var text = File.ReadAllText(SentinelPath);
        Assert.Contains("a1b2c3d", text, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hooks_arm_on_install_and_disarm_on_dispose()
    {
        var hooks = new CrashHooks(
            new ClientCapture(new Redactor(), new FixedClientInfoProvider()),
            _queue,
            () => _settings,
            null,
            Samples.Context(),
            _clock,
            Sentinel());

        Assert.False(File.Exists(SentinelPath));
        hooks.Install();
        Assert.True(File.Exists(SentinelPath));
        hooks.Dispose();
        Assert.False(File.Exists(SentinelPath));
        Assert.Empty(_queue.List());
    }

    [Fact]
    public void Hooks_that_never_install_leave_a_stale_sentinel_alone()
    {
        Sentinel().Arm();
        var hooks = new CrashHooks(
            new ClientCapture(new Redactor(), new FixedClientInfoProvider()),
            _queue,
            () => _settings,
            null,
            Samples.Context(),
            _clock,
            Sentinel());

        hooks.Dispose();

        Assert.True(File.Exists(SentinelPath));
    }
}
