using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Tests;

public class NativeSizeTests
{
    private static readonly byte[] _big = Images.Png(400, 300);

    private static Rig SmallLimitsRig()
    {
        var r = new Rig();
        r.Options.MaxWidth = 200;
        r.Options.MaxHeight = 150;
        r.Guest.OnGetFile = (_, s) => s.Write(_big);
        r.Browser.Png = () => _big;
        r.Hypervisor.Frame = () => new RawFrame(RawPixelFormat.Png, _big, 0, 0);
        return r;
    }

    private static Task<ScreenFrame> Capture(GuestDisplayService s, DisplayTarget t, DisplaySourceKind source) =>
        s.CaptureAsync(new CaptureRequest(t, source), TestContext.Current.CancellationToken);

    [Fact]
    public void Unknown_before_any_capture()
    {
        GuestDisplayService s = SmallLimitsRig().Build();
        Assert.Null(s.TryGetNativeSize(Rig.Linux()));
    }

    [Fact]
    public async Task A_downscaled_guest_capture_records_the_size_before_downscaling()
    {
        GuestDisplayService s = SmallLimitsRig().Build();
        ScreenFrame f = await Capture(s, Rig.Linux(), DisplaySourceKind.Guest);
        Assert.Equal((200, 150), (f.Width, f.Height));
        Assert.Equal((400, 300), s.TryGetNativeSize(Rig.Linux()));
        Assert.Null(s.TryGetNativeSize(Rig.Windows()));
    }

    [Fact]
    public async Task Hypervisor_and_browser_captures_record_nothing()
    {
        GuestDisplayService s = SmallLimitsRig().Build();
        await Capture(s, Rig.Linux(), DisplaySourceKind.Hypervisor);
        await Capture(s, Rig.Linux("session"), DisplaySourceKind.Browser);
        Assert.Null(s.TryGetNativeSize(Rig.Linux()));
        Assert.Null(s.TryGetNativeSize(Rig.Linux("session")));
    }

    [Fact]
    public async Task A_pixel_exact_browser_capture_records_the_size_per_session()
    {
        Rig r = SmallLimitsRig();
        r.PixelExactBrowser = true;
        GuestDisplayService s = r.Build();
        ScreenFrame f = await Capture(s, Rig.Linux("session"), DisplaySourceKind.Browser);
        Assert.Equal((200, 150), (f.Width, f.Height));
        Assert.Equal((400, 300), s.TryGetNativeSize(Rig.Linux("session")));
        Assert.Null(s.TryGetNativeSize(Rig.Linux("other")));
        Assert.Null(s.TryGetNativeSize(Rig.Linux()));
    }

    [Fact]
    public async Task Browser_sizes_are_capped_oldest_out()
    {
        Rig r = SmallLimitsRig();
        r.PixelExactBrowser = true;
        r.Options.CapturesPerMinute = 100_000;
        GuestDisplayService s = r.Build();
        for (int i = 0; i <= GuestDisplayService.MaxRememberedDisplays; i++)
            await Capture(s, Rig.Linux("s" + i), DisplaySourceKind.Browser);
        Assert.Null(s.TryGetNativeSize(Rig.Linux("s0")));
        Assert.Equal((400, 300), s.TryGetNativeSize(Rig.Linux("s" + GuestDisplayService.MaxRememberedDisplays)));
    }

    [Fact]
    public async Task A_browser_session_of_the_same_vm_does_not_see_the_vm_display_size()
    {
        GuestDisplayService s = SmallLimitsRig().Build();
        await Capture(s, Rig.Linux(), DisplaySourceKind.Guest);
        Assert.Equal((400, 300), s.TryGetNativeSize(Rig.Linux()));
        Assert.Null(s.TryGetNativeSize(Rig.Linux("session")));
    }

    [Fact]
    public async Task The_newest_size_wins_and_the_map_is_capped_oldest_out()
    {
        var r = SmallLimitsRig();
        r.Options.CapturesPerMinute = 100_000;
        GuestDisplayService s = r.Build();
        DisplayTarget first = new("a", Guid.NewGuid(), DisplayGuestOs.Linux, "vm");
        await Capture(s, first, DisplaySourceKind.Guest);
        for (int i = 0; i < GuestDisplayService.MaxRememberedDisplays; i++)
            await Capture(s, new DisplayTarget("a", Guid.NewGuid(), DisplayGuestOs.Linux, "vm"), DisplaySourceKind.Guest);
        Assert.Null(s.TryGetNativeSize(first));

        DisplayTarget last = new("a", Guid.NewGuid(), DisplayGuestOs.Linux, "vm");
        await Capture(s, last, DisplaySourceKind.Guest);
        Assert.Equal((400, 300), s.TryGetNativeSize(last));
    }
}
