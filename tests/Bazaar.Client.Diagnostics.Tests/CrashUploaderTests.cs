using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Consent;

namespace Bazaar.Client.Diagnostics.Tests;

public class CrashUploaderTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _root = new();
    private readonly TestClock _clock = new(_start);
    private readonly FakeTransport _transport = new();
    private readonly CrashReportQueue _queue;
    private ConsentSettings _settings = Samples.Mode(ConsentMode.Ask);

    public CrashUploaderTests()
    {
        _queue = new CrashReportQueue(_root.Sub("queue"), CrashQueueLimits.Default, _clock);
    }

    public void Dispose()
    {
        _root.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Guid Id(int n) => new($"00000000-0000-7000-8000-{n:D12}");

    private CrashUploader Uploader(CrashUploadOptions? options = null) =>
        new(
            _queue,
            _transport,
            () => _settings,
            _clock,
            options ?? CrashUploadOptions.Default,
            (_, _) => Task.CompletedTask,
            () => 0.5);

    private void Enqueue(int n, string residual = "pass", DateTimeOffset? createdAt = null) =>
        _queue.Add(Samples.Report(Id(n), createdAt ?? _clock.GetUtcNow(), residual));

    private Guid[] Queued() => _queue.List().Select(e => e.ReportId).ToArray();

    [Fact]
    public async Task Consent_off_uploads_nothing()
    {
        _settings = Samples.Mode(ConsentMode.Off);
        Enqueue(1);

        var summary = await Uploader().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(CrashUploadSummary.Empty, summary);
        Assert.Empty(_transport.Submitted);
        Assert.Equal([Id(1)], Queued());
    }

    [Fact]
    public async Task Ask_mode_uploads_only_the_reports_the_person_approved()
    {
        Enqueue(1);
        Enqueue(2);
        var uploader = Uploader();
        uploader.Approve(Id(2));

        var summary = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal([Id(2)], _transport.Submitted);
        Assert.Equal(1, summary.Sent);
        Assert.Equal(1, summary.AwaitingApproval);
        Assert.Equal([Id(1)], Queued());
    }

    [Fact]
    public async Task Auto_mode_uploads_without_approval()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);

        var summary = await Uploader().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Sent);
        Assert.Equal([Id(1)], _transport.Submitted);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task A_report_is_deleted_only_after_its_ack()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        _transport.Respond = _ => null;
        var uploader = Uploader();

        var first = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, first.Retried);
        Assert.Equal([Id(1)], Queued());

        _clock.Advance(TimeSpan.FromMinutes(2));
        _transport.Respond = submit => new CrashAck(submit.Report.ReportId, "group-1");
        var second = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, second.Sent);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task An_ack_for_another_report_does_not_delete_this_one()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        _transport.Respond = _ => new CrashAck(Id(99), "group-1");

        var summary = await Uploader().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Retried);
        Assert.Equal([Id(1)], Queued());
    }

    [Fact]
    public async Task A_transient_failure_waits_for_the_backoff_before_the_next_attempt()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        _transport.Fail = new HttpRequestException("connection reset");
        var uploader = Uploader();

        var first = await uploader.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, first.Retried);
        Assert.Single(_transport.Submitted);

        var early = await uploader.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, early.Waiting);
        Assert.Single(_transport.Submitted);

        _clock.Advance(TimeSpan.FromMinutes(2));
        _transport.Fail = null;
        var later = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, later.Sent);
        Assert.Equal(2, _transport.Submitted.Count);
    }

    [Fact]
    public async Task A_refusal_is_permanent_and_the_report_is_quarantined()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        _transport.Fail = new CrashRefusedException("bad request");
        var uploader = Uploader();

        var first = await uploader.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, first.Quarantined);
        Assert.Empty(Queued());
        Assert.True(File.Exists(Path.Combine(_root.Sub("queue"), Id(1).ToString("D") + ".refused")));

        _clock.Advance(TimeSpan.FromDays(1));
        var later = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(CrashUploadSummary.Empty, later);
        Assert.Single(_transport.Submitted);
    }

    [Fact]
    public async Task The_attempt_limit_drops_the_report_without_an_ack()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        _transport.Fail = new TimeoutException("slow");
        var uploader = Uploader(CrashUploadOptions.Default with { MaxAttempts = 2 });

        var first = await uploader.DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, first.Retried);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, second.Dropped);
        Assert.Empty(Queued());
        Assert.Equal(2, _transport.Submitted.Count);
    }

    [Fact]
    public async Task Reports_past_the_age_limit_are_dropped_without_a_send()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1, createdAt: _start);
        _clock.Advance(TimeSpan.FromDays(2));

        var summary = await Uploader(CrashUploadOptions.Default with { MaxAge = TimeSpan.FromDays(1) }).DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Dropped);
        Assert.Empty(_transport.Submitted);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task The_rate_limit_spreads_sends_over_minutes()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        Enqueue(2);
        Enqueue(3);
        var uploader = Uploader(CrashUploadOptions.Default with { MaxPerMinute = 2 });

        var first = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(2, first.Sent);
        Assert.True(first.RateLimited);
        Assert.Equal(2, _transport.Submitted.Count);

        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await uploader.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, second.Sent);
        Assert.Equal(3, _transport.Submitted.Count);
    }

    [Fact]
    public async Task A_report_whose_redaction_did_not_pass_is_never_uploaded()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1, residual: "fail");

        var summary = await Uploader().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Held);
        Assert.Empty(_transport.Submitted);
        Assert.Equal([Id(1)], Queued());
    }

    [Fact]
    public async Task A_report_that_fails_validation_is_quarantined_before_any_send()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        _queue.Add(Samples.Report(Id(1), _start, schema: "bazaar.crash/99"));

        var summary = await Uploader().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, summary.Quarantined);
        Assert.Empty(_transport.Submitted);
        Assert.Empty(Queued());
    }

    [Fact]
    public async Task Run_returns_at_once_when_the_token_is_already_cancelled()
    {
        _settings = Samples.Mode(ConsentMode.Auto);
        Enqueue(1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Uploader().RunAsync(cancelled.Token);

        Assert.Empty(_transport.Submitted);
    }

    private sealed class FakeTransport : ICrashTransport
    {
        public List<Guid> Submitted { get; } = [];

        public Func<CrashSubmit, CrashAck?> Respond { get; set; } = submit => new CrashAck(submit.Report.ReportId, "group-1");

        /// <summary>When set, every send throws this instead of answering.</summary>
        public Exception? Fail { get; set; }

        public Task<CrashAck?> SubmitAsync(CrashSubmit submit, CancellationToken cancellationToken)
        {
            Submitted.Add(submit.Report.ReportId);
            if (Fail is not null)
            {
                return Task.FromException<CrashAck?>(Fail);
            }

            return Task.FromResult(Respond(submit));
        }
    }
}
