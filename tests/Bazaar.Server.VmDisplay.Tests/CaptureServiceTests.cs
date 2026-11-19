using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Tests;

public class CaptureServiceTests
{
    private static readonly byte[] s_png = Images.Png(64, 48);

    private static Rig WorkingRig()
    {
        var r = new Rig();
        r.Hypervisor.Frame = () => new RawFrame(RawPixelFormat.Png, s_png, 0, 0);
        r.Guest.OnGetFile = (_, s) => s.Write(s_png);
        r.Browser.Png = () => s_png;
        return r;
    }

    private static async Task<DisplayException> Fails(Task t) => await Assert.ThrowsAsync<DisplayException>(() => t);

    // Source selection

    [Fact]
    public async Task Linux_prefers_guest_then_hypervisor()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Guest, f.Source);
        Assert.Equal(0, r.Hypervisor.Reads);
    }

    [Fact]
    public async Task Windows_uses_the_hypervisor_and_never_the_guest_source()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Hypervisor, f.Source);
        Assert.Empty(r.Guest.Execs);
    }

    [Fact]
    public async Task A_browser_session_uses_the_browser_source_first()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux("sess-9")), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Browser, f.Source);
        Assert.Equal("sess-9", r.Browser.LastSession);
        Assert.Empty(r.Guest.Execs);
    }

    [Fact]
    public async Task A_browser_session_the_driver_does_not_support_is_skipped()
    {
        Rig r = WorkingRig();
        r.Browser.Supported = false;
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux("sess-9")), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Guest, f.Source);
    }

    [Fact]
    public async Task Linux_falls_back_from_guest_to_hypervisor_and_records_the_producer()
    {
        Rig r = WorkingRig();
        r.Guest.ExitFor = _ => 1;
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Hypervisor, f.Source);
        Assert.Equal(1, r.Hypervisor.Reads);
    }

    [Fact]
    public async Task A_failed_browser_source_falls_through_to_the_guest()
    {
        Rig r = WorkingRig();
        r.Browser.Fail = true;
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux("s")), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Guest, f.Source);
    }

    [Fact]
    public async Task A_malformed_frame_from_one_source_falls_through_to_the_next()
    {
        Rig r = WorkingRig();
        r.Guest.OnGetFile = (_, s) => s.Write([1, 2, 3, 4]);
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Hypervisor, f.Source);
    }

    [Fact]
    public async Task A_named_source_never_falls_back()
    {
        Rig r = WorkingRig();
        r.Guest.ExitFor = _ => 1;
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Linux(), DisplaySourceKind.Guest), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Equal(0, r.Hypervisor.Reads);
    }

    [Fact]
    public async Task A_named_source_is_used_even_when_another_is_preferred()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux(), DisplaySourceKind.Hypervisor), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Hypervisor, f.Source);
        Assert.Empty(r.Guest.Execs);
    }

    [Fact]
    public async Task A_named_source_that_is_unavailable_reports_no_source()
    {
        Rig r = WorkingRig();
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Windows(), DisplaySourceKind.Guest), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.NoSourceAvailable, e.Failure);
    }

    [Fact]
    public async Task No_ports_means_no_source_available()
    {
        var r = new Rig { UseBrowser = false, UseGuest = false, UseHypervisor = false };
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.NoSourceAvailable, e.Failure);
    }

    [Fact]
    public async Task The_module_works_with_only_a_browser_driver()
    {
        Rig r = WorkingRig();
        r.UseGuest = false;
        r.UseHypervisor = false;
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Windows("s")), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Browser, f.Source);
    }

    [Fact]
    public async Task When_every_source_fails_the_last_failure_is_reported()
    {
        Rig r = WorkingRig();
        r.Guest.ExitFor = _ => 1;
        r.Hypervisor.Fail = true;
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
    }

    [Fact]
    public async Task Windows_with_a_failing_hypervisor_has_nothing_to_fall_back_to()
    {
        Rig r = WorkingRig();
        r.Hypervisor.Fail = true;
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
    }

    [Fact]
    public void Available_sources_are_listed_in_preference_order()
    {
        Rig r = WorkingRig();
        GuestDisplayService s = r.Build();
        Assert.Equal([DisplaySourceKind.Guest, DisplaySourceKind.Hypervisor], s.AvailableSources(Rig.Linux()));
        Assert.Equal([DisplaySourceKind.Browser, DisplaySourceKind.Guest, DisplaySourceKind.Hypervisor], s.AvailableSources(Rig.Linux("x")));
        Assert.Equal([DisplaySourceKind.Hypervisor], s.AvailableSources(Rig.Windows()));
        Assert.Equal([DisplaySourceKind.Browser, DisplaySourceKind.Hypervisor], s.AvailableSources(Rig.Windows("x")));
    }

    // Checks and limits

    [Fact]
    public async Task A_disabled_module_refuses_and_lists_no_sources()
    {
        Rig r = WorkingRig();
        r.Options.Enabled = false;
        GuestDisplayService s = r.Build();
        DisplayException e = await Fails(s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.Disabled, e.Failure);
        Assert.Empty(s.AvailableSources(Rig.Linux()));
        Assert.Empty(r.Guest.Execs);
    }

    [Fact]
    public async Task The_gate_can_refuse_a_capture_before_any_source_is_touched()
    {
        Rig r = WorkingRig();
        r.Gate.DenyReason = "kill switch";
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.Refused, e.Failure);
        Assert.Contains("kill switch", e.Message);
        Assert.Empty(r.Guest.Execs);
        Assert.Equal(0, r.Hypervisor.Reads);
        Assert.Contains("refused:capture:kill switch", r.Audit.Rows);
    }

    [Fact]
    public async Task A_throwing_gate_fails_closed()
    {
        Rig r = WorkingRig();
        r.Gate.Throw = true;
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.Refused, e.Failure);
        Assert.Empty(r.Guest.Execs);
    }

    [Fact]
    public async Task Capture_rate_limit_is_per_agent_and_uses_the_time_provider()
    {
        Rig r = WorkingRig();
        r.Options.CapturesPerMinute = 2;
        GuestDisplayService s = r.Build();
        await s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        await s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        DisplayException e = await Fails(s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.RateLimited, e.Failure);

        // Another agent has its own budget.
        await s.CaptureAsync(new CaptureRequest(Rig.Linux(agent: "agent-2")), TestContext.Current.CancellationToken);

        r.Time.Advance(TimeSpan.FromSeconds(61));
        await s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_refused_capture_does_not_use_up_the_rate_limit()
    {
        Rig r = WorkingRig();
        r.Options.CapturesPerMinute = 1;
        r.Gate.DenyReason = "no";
        GuestDisplayService s = r.Build();
        for (int i = 0; i < 3; i++)
            Assert.Equal(DisplayFailure.Refused, (await Fails(s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken))).Failure);
        r.Gate.DenyReason = null;
        await s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_invalid_target_is_rejected_and_audited()
    {
        Rig r = WorkingRig();
        GuestDisplayService s = r.Build();
        DisplayTarget noAgent = Rig.Linux(agent: " ");
        DisplayTarget noVm = Rig.Linux() with { VmId = Guid.Empty };
        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(s.CaptureAsync(new CaptureRequest(noAgent), TestContext.Current.CancellationToken))).Failure);
        Assert.Equal(DisplayFailure.InvalidInput, (await Fails(s.CaptureAsync(new CaptureRequest(noVm), TestContext.Current.CancellationToken))).Failure);
        Assert.Equal(2, r.Audit.Rows.Count(x => x.StartsWith("refused:capture", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancellation_propagates_and_is_not_treated_as_a_source_failure()
    {
        Rig r = WorkingRig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), cts.Token));
    }

    // Audit and normalisation

    [Fact]
    public async Task Audit_receives_sizes_and_hash_never_pixels()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        (DisplaySourceKind source, int w, int h, int bytes, string sha) = Assert.Single(r.Audit.Captures);
        Assert.Equal(DisplaySourceKind.Guest, source);
        Assert.Equal((64, 48), (w, h));
        Assert.Equal(f.Png.Length, bytes);
        Assert.Equal(f.Sha256, sha);
        Assert.Equal(ScreenFrame.ComputeSha256(s_png), sha);
        Assert.Equal(64, sha.Length);
        // The sink interface has no byte[] parameter at all; this guards the contract shape.
        Assert.DoesNotContain(typeof(IDisplayAuditSink).GetMethods().SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(byte[]));
    }

    [Fact]
    public async Task The_frame_carries_the_capture_time_from_the_time_provider()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        Assert.Equal(r.Time.GetUtcNow(), f.CapturedAt);
    }

    [Fact]
    public async Task A_png_within_limits_reaches_the_caller_untouched()
    {
        Rig r = WorkingRig();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux("s")), TestContext.Current.CancellationToken);
        Assert.Same(s_png, f.Png);
        Assert.Equal((64, 48), (f.Width, f.Height));
    }

    [Fact]
    public async Task An_oversized_frame_is_downscaled_to_the_configured_limit()
    {
        Rig r = WorkingRig();
        r.Options.MaxWidth = 100;
        r.Options.MaxHeight = 100;
        byte[] big = Images.Png(400, 200);
        r.Browser.Png = () => big;
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux("s")), TestContext.Current.CancellationToken);
        Assert.Equal((100, 50), (f.Width, f.Height));
    }

    [Fact]
    public async Task The_hard_ceiling_applies_even_when_configured_higher()
    {
        Rig r = WorkingRig();
        r.Options.MaxWidth = 100_000;
        r.Options.MaxHeight = 100_000;
        r.Hypervisor.Frame = () => new RawFrame(RawPixelFormat.Rgb565, new byte[8 * 8 * 2], 8, 8);
        await r.Build().CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken);
        Assert.Equal((DisplayLimits.CeilingWidth, DisplayLimits.CeilingHeight), r.Hypervisor.LastRequestedSize);
    }

    [Fact]
    public async Task A_raw_hypervisor_frame_is_normalised_to_png()
    {
        Rig r = WorkingRig();
        r.Hypervisor.Frame = () => new RawFrame(RawPixelFormat.Rgb565, new byte[16 * 8 * 2], 16, 8);
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken);
        Assert.Equal((16, 8), (f.Width, f.Height));
        Assert.Equal(0x89, f.Png[0]);
    }

    [Fact]
    public async Task A_malformed_frame_from_the_only_source_is_reported_as_malformed()
    {
        Rig r = WorkingRig();
        r.Hypervisor.Frame = () => new RawFrame(RawPixelFormat.Rgb565, new byte[3], 16, 8);
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.FrameMalformed, e.Failure);
    }

    [Fact]
    public async Task An_encoded_frame_over_the_byte_limit_is_rejected()
    {
        Rig r = WorkingRig();
        r.Options.MaxEncodedBytes = 100;
        DisplayException e = await Fails(r.Build().CaptureAsync(new CaptureRequest(Rig.Windows(), DisplaySourceKind.Hypervisor), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.FrameTooLarge, e.Failure);
    }

    [Fact]
    public void Invalid_options_are_rejected_at_construction()
    {
        var r = new Rig();
        r.Options.CapturesPerMinute = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Build());
    }
}
