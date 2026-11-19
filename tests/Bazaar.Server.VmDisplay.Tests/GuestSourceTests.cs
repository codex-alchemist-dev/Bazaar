using System.Text.RegularExpressions;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Tests;

public partial class GuestSourceTests
{
    private static readonly byte[] _png = Images.Png(32, 32);

    [GeneratedRegex("^/run/bazaar/display/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\.png$")]
    private static partial Regex ServerPath();

    private static Rig GuestOnly()
    {
        var r = new Rig { UseBrowser = false, UseHypervisor = false };
        r.Guest.OnGetFile = (_, s) => s.Write(_png);
        return r;
    }

    [Fact]
    public async Task Capture_runs_grim_with_a_server_generated_path_then_fetches_then_removes()
    {
        Rig r = GuestOnly();
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);

        Assert.Equal(DisplaySourceKind.Guest, f.Source);
        Assert.Equal(2, r.Guest.Execs.Count);
        IReadOnlyList<string> grim = r.Guest.Execs[0];
        Assert.Equal(["grim", "-t", "png"], grim.Take(3));
        Assert.Matches(ServerPath(), grim[3]);
        Assert.Equal(4, grim.Count);
        Assert.Equal(grim[3], r.Guest.FetchedPath);
        Assert.Equal(["rm", "-f", grim[3]], r.Guest.Execs[1]);
    }

    [Fact]
    public async Task Each_capture_uses_a_fresh_path()
    {
        Rig r = GuestOnly();
        GuestDisplayService s = r.Build();
        await s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        await s.CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        Assert.NotEqual(r.Guest.Execs[0][3], r.Guest.Execs[2][3]);
    }

    [Fact]
    public async Task Cleanup_still_runs_after_a_failed_file_fetch()
    {
        Rig r = GuestOnly();
        r.Guest.GetFileFails = true;
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Equal("rm", r.Guest.Execs[^1][0]);
        Assert.Equal("-f", r.Guest.Execs[^1][1]);
        Assert.Equal(r.Guest.Execs[0][3], r.Guest.Execs[^1][2]);
    }

    [Fact]
    public async Task A_non_zero_grim_exit_is_a_source_failure_and_nothing_is_fetched()
    {
        Rig r = GuestOnly();
        r.Guest.ExitFor = argv => argv[0] == "grim" ? 3 : 0;
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Null(r.Guest.FetchedPath);
        Assert.Equal("rm", r.Guest.Execs[^1][0]);
    }

    [Fact]
    public async Task A_failing_cleanup_does_not_mask_the_frame()
    {
        Rig r = GuestOnly();
        r.Guest.ExitFor = argv => argv[0] == "rm" ? 1 : 0;
        ScreenFrame f = await r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken);
        Assert.Equal(32, f.Width);
    }

    [Fact]
    public async Task The_fetch_sink_is_capped_and_oversized_files_are_rejected()
    {
        Rig r = GuestOnly();
        r.Options.MaxEncodedBytes = 1000;
        r.Guest.OnGetFile = (_, s) =>
        {
            var chunk = new byte[400];
            for (int i = 0; i < 10; i++)
                s.Write(chunk);
        };
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.FrameTooLarge, e.Failure);
        Assert.Equal("rm", r.Guest.Execs[^1][0]);
    }

    [Fact]
    public void Capped_stream_stops_at_the_cap_exactly()
    {
        using var s = new CappedSinkStream(10);
        s.Write(new byte[10]);
        Assert.Equal(10, s.Length);
        Assert.Equal(DisplayFailure.FrameTooLarge, Assert.Throws<DisplayException>(() => s.WriteByte(1)).Failure);
        Assert.False(s.CanRead);
        Assert.Throws<NotSupportedException>(() => s.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public async Task A_guest_that_returns_garbage_is_rejected_as_malformed()
    {
        Rig r = GuestOnly();
        r.Guest.OnGetFile = (_, s) => s.Write("not a png at all, just text that is long enough to pass length checks"u8);
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.FrameMalformed, e.Failure);
    }

    [Fact]
    public async Task A_guest_exec_adapter_that_does_not_support_the_target_is_not_used()
    {
        Rig r = GuestOnly();
        r.Guest.Supported = false;
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => r.Build().CaptureAsync(new CaptureRequest(Rig.Linux()), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.NoSourceAvailable, e.Failure);
        Assert.Empty(r.Guest.Execs);
    }
}
