using System.Text.Json;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Canaries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class CanarySinkTests
{
    private readonly FakeAudit _audit = new();
    private readonly FakeContainment _containment = new();

    private ContainingCanarySink Sink(bool withContainment = true, CanarySinkOptions? options = null)
    {
        var services = new ServiceCollection();
        if (withContainment)
            services.AddSingleton<IAgentContainment>(_containment);
        return new ContainingCanarySink(
            _audit,
            services.BuildServiceProvider(),
            TimeProvider.System,
            options ?? new CanarySinkOptions { ContainmentTimeout = TimeSpan.FromMilliseconds(300) },
            NullLogger<ContainingCanarySink>.Instance);
    }

    private static CanaryUseReport Report(Guid? agent = null, string canaryId = "fp-1", string path = ".canary/credentials.json", string source = "provider") =>
        new(agent ?? Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.NewGuid(), Guid.NewGuid(), canaryId, path, CanaryUseKind.FileRead, source, DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_trip_is_audited_then_contained_then_audited_again()
    {
        var sink = Sink();
        var report = Report();

        await sink.ReportCanaryUseAsync(report, TestContext.Current.CancellationToken);

        var call = Assert.Single(_containment.Calls);
        Assert.Equal(report.AgentId.ToString("D"), call.AgentId);
        Assert.Equal("vm-mounts-canary", call.Actor);
        Assert.Equal(["vm.mount.canary_trip", "vm.mount.canary_contained"], _audit.Entries.Select(e => e.Kind));
        Assert.All(_audit.Entries, e => Assert.Equal("vm-mounts", e.Actor));
        Assert.Equal(0, sink.PendingCount);
    }

    [Fact]
    public async Task The_audit_payload_names_the_fingerprint_and_cleans_observer_text()
    {
        var sink = Sink();

        await sink.ReportCanaryUseAsync(Report(source: "proxy\u001b[31m\nforged line"), TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(_audit.Entries[0].Payload);
        Assert.Equal("fp-1", json.RootElement.GetProperty("canary_id").GetString());
        Assert.DoesNotContain('\u001b', json.RootElement.GetProperty("source").GetString()!);
        Assert.DoesNotContain('\n', json.RootElement.GetProperty("source").GetString()!);
        Assert.False(json.RootElement.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task A_retired_mount_still_triggers_containment()
    {
        var sink = Sink();

        await sink.ReportCanaryUseAsync(Report() with { Retired = true }, TestContext.Current.CancellationToken);

        Assert.Single(_containment.Calls);
    }

    [Fact]
    public async Task A_failed_containment_is_queued_surfaced_and_retried_until_it_works()
    {
        _containment.FailNext = 2;
        var sink = Sink();
        var report = Report();

        var ex = await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(report, TestContext.Current.CancellationToken).AsTask());
        Assert.Contains("queued", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, sink.PendingCount);
        Assert.Equal(1, await sink.RetryPendingAsync());
        Assert.Equal(0, await sink.RetryPendingAsync());

        Assert.Equal(3, _containment.Calls.Count);
        // The failure is on record once, however many retries it took, and the success is recorded too.
        Assert.Equal(
            ["vm.mount.canary_trip", "vm.mount.canary_containment_failed", "vm.mount.canary_contained"],
            _audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task A_failed_audit_does_not_delay_containment_and_is_retried()
    {
        _audit.FailNext = 1;
        var sink = Sink();

        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken).AsTask());

        // Containment ran on the first pass even though the first audit write failed.
        Assert.Single(_containment.Calls);
        Assert.Equal(0, await sink.RetryPendingAsync());
        Assert.Equal(["vm.mount.canary_contained", "vm.mount.canary_trip"], _audit.Entries.Select(e => e.Kind));
        // Containment is not repeated once it has succeeded.
        Assert.Single(_containment.Calls);
    }

    [Fact]
    public async Task An_audit_store_that_stays_down_keeps_the_trip_queued()
    {
        _audit.FailNext = -1;
        var sink = Sink();

        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, await sink.RetryPendingAsync());
        Assert.Equal(1, sink.PendingCount);

        _audit.FailNext = 0;
        Assert.Equal(0, await sink.RetryPendingAsync());
        Assert.Equal(2, _audit.Entries.Count);
    }

    [Fact]
    public async Task Missing_containment_is_never_a_silent_pass()
    {
        var sink = Sink(withContainment: false);

        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, sink.PendingCount);
        Assert.Contains(_audit.Entries, e => e.Kind == "vm.mount.canary_containment_failed");
    }

    [Fact]
    public async Task A_hung_containment_is_cut_off_and_stays_queued()
    {
        _containment.Hang = true;
        var sink = Sink();

        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, sink.PendingCount);
    }

    [Fact]
    public async Task Repeats_of_a_pending_trip_share_one_queue_entry()
    {
        _containment.FailNext = -1;
        var sink = Sink();
        var report = Report();

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(report, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(1, sink.PendingCount);
        // The failure record is written once, not on every repeat.
        Assert.Single(_audit.Entries, e => e.Kind == "vm.mount.canary_containment_failed");
    }

    [Fact]
    public async Task Different_canaries_are_different_trips()
    {
        _containment.FailNext = -1;
        var sink = Sink();

        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(canaryId: "a"), TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(canaryId: "b"), TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, sink.PendingCount);
    }

    [Fact]
    public async Task A_full_queue_refuses_loudly_instead_of_dropping()
    {
        _containment.FailNext = -1;
        var sink = Sink(options: new CanarySinkOptions { MaxPending = 1, ContainmentTimeout = TimeSpan.FromMilliseconds(300) });
        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(canaryId: "a"), TestContext.Current.CancellationToken).AsTask());

        var ex = await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(canaryId: "b"), TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("full", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, sink.PendingCount);
    }

    [Fact]
    public async Task A_report_without_an_agent_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => Sink().ReportCanaryUseAsync(Report(agent: Guid.Empty) with { AgentId = Guid.Empty }, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task The_retry_service_finishes_pending_trips_in_the_background()
    {
        _containment.FailNext = 1;
        var options = new CanarySinkOptions
        {
            RetryInitialDelay = TimeSpan.FromMilliseconds(10),
            RetryMaxDelay = TimeSpan.FromMilliseconds(40),
            ContainmentTimeout = TimeSpan.FromMilliseconds(300),
        };
        var sink = Sink(options: options);
        await Assert.ThrowsAsync<CanaryTripPendingException>(() => sink.ReportCanaryUseAsync(Report(), TestContext.Current.CancellationToken).AsTask());

        var service = new CanaryTripRetryService(sink, TimeProvider.System);
        await service.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (sink.PendingCount > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, sink.PendingCount);
        Assert.Equal(2, _containment.Calls.Count);
    }
}
