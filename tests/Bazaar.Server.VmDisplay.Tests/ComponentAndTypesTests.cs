using Bazaar.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmDisplay.Tests;

public class ComponentAndTypesTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Provider(Action<IServiceCollection>? before = null, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        new VmDisplayComponent().AddServices(services, config ?? Config());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_matches_the_design()
    {
        ComponentManifest m = new VmDisplayComponent().Manifest;
        Assert.Equal("vm-display", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vms", "browser", "security", "audit"], m.Soft);
        Assert.Equal(["vm-display:capture", "vm-display:input"], m.Provides);
    }

    [Fact]
    public void Component_registers_the_service_and_defaults()
    {
        using ServiceProvider sp = Provider();
        Assert.IsType<GuestDisplayService>(sp.GetRequiredService<IGuestDisplay>());
        Assert.Same(TimeProvider.System, sp.GetRequiredService<TimeProvider>());
        Assert.IsType<NoopDisplayAuditSink>(sp.GetRequiredService<IDisplayAuditSink>());
        Assert.IsType<UnconfiguredDisplayGate>(sp.GetRequiredService<IDisplayGate>());
    }

    [Fact]
    public async Task The_unconfigured_gate_fails_closed_for_capture_and_input()
    {
        var fake = new FakeHypervisor { Frame = () => new RawFrame(RawPixelFormat.Png, Images.Png(8, 8), 0, 0) };
        using ServiceProvider sp = Provider(s => s.AddSingleton<IHypervisorConsole>(fake));
        IGuestDisplay display = sp.GetRequiredService<IGuestDisplay>();

        DisplayException cap = await Assert.ThrowsAsync<DisplayException>(() => display.CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken));
        DisplayException inp = await Assert.ThrowsAsync<DisplayException>(() => display.SendInputAsync(new InputBatch(Rig.Windows(), [new KeyStroke("Tab")]), TestContext.Current.CancellationToken));
        Assert.Equal(DisplayFailure.Refused, cap.Failure);
        Assert.Equal(DisplayFailure.Refused, inp.Failure);
        Assert.Contains("no display gate configured", cap.Message);
        Assert.Equal(0, fake.Reads);
        Assert.Empty(fake.Inputs);
    }

    [Fact]
    public async Task A_host_supplied_gate_and_ports_are_respected_by_try_add()
    {
        var audit = new RecordingAudit();
        var fake = new FakeHypervisor { Frame = () => new RawFrame(RawPixelFormat.Png, Images.Png(8, 8), 0, 0) };
        using ServiceProvider sp = Provider(s =>
        {
            s.AddSingleton<IDisplayGate>(new AllowAllDisplayGate());
            s.AddSingleton<IDisplayAuditSink>(audit);
            s.AddSingleton<TimeProvider>(new FakeTimeProvider());
            s.AddSingleton<IHypervisorConsole>(fake);
        });
        ScreenFrame f = await sp.GetRequiredService<IGuestDisplay>().CaptureAsync(new CaptureRequest(Rig.Windows()), TestContext.Current.CancellationToken);
        Assert.Equal(DisplaySourceKind.Hypervisor, f.Source);
        Assert.Single(audit.Captures);
    }

    [Fact]
    public void Options_bind_from_the_vm_display_section()
    {
        VmDisplayOptions o = VmDisplayOptions.Bind(Config(
            ("Bazaar:VmDisplay:Enabled", "false"),
            ("Bazaar:VmDisplay:MaxWidth", "1280"),
            ("Bazaar:VmDisplay:CapturesPerMinute", "5")));
        Assert.False(o.Enabled);
        Assert.Equal(1280, o.MaxWidth);
        Assert.Equal(5, o.CapturesPerMinute);
        Assert.Equal(1200, o.MaxHeight);
    }

    [Fact]
    public void Options_have_the_documented_defaults()
    {
        var o = new VmDisplayOptions();
        Assert.True(o.Enabled);
        Assert.Equal((1920, 1200), (o.MaxWidth, o.MaxHeight));
        Assert.Equal((60, 120, 10), (o.CapturesPerMinute, o.InputBatchesPerMinute, o.GuestCaptureTimeoutSeconds));
        Assert.Equal(32 * 1024 * 1024, o.MaxEncodedBytes);
        Assert.Equal((200, 2000), (o.MaxActionsPerBatch, o.MaxTextLength));
    }

    [Fact]
    public void Configured_sizes_above_the_ceiling_are_clamped_and_bad_values_rejected()
    {
        VmDisplayOptions o = VmDisplayOptions.Bind(Config(("Bazaar:VmDisplay:MaxWidth", "9000"), ("Bazaar:VmDisplay:MaxHeight", "9000")));
        Assert.Equal(3840, o.EffectiveMaxWidth);
        Assert.Equal(2160, o.EffectiveMaxHeight);
        Assert.Throws<ArgumentOutOfRangeException>(() => VmDisplayOptions.Bind(Config(("Bazaar:VmDisplay:MaxTextLength", "0"))));
        Assert.Throws<ArgumentOutOfRangeException>(() => Provider(config: Config(("Bazaar:VmDisplay:CapturesPerMinute", "-3"))));
    }

    // Content hiding

    [Fact]
    public void Screen_frame_to_string_never_prints_pixel_bytes()
    {
        byte[] secretBytes = System.Text.Encoding.ASCII.GetBytes(string.Concat("PIXEL", "-", "MARKER"));
        var f = new ScreenFrame(secretBytes, 10, 5, DisplaySourceKind.Guest, DateTimeOffset.UnixEpoch, "abc");
        string s = f.ToString();
        Assert.DoesNotContain("MARKER", s, StringComparison.Ordinal);
        Assert.DoesNotContain("83, 69", s, StringComparison.Ordinal);
        Assert.Contains("10x5", s, StringComparison.Ordinal);
        Assert.Contains("abc", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_text_to_string_hides_the_text()
    {
        string s = new TypeText("correct horse battery staple").ToString();
        Assert.DoesNotContain("horse", s, StringComparison.Ordinal);
        Assert.Contains("28 chars", s, StringComparison.Ordinal);
        Assert.DoesNotContain("horse", new InputBatch(Rig.Linux(), [new TypeText("correct horse")]).Actions[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_frame_to_string_hides_bytes()
    {
        string s = new RawFrame(RawPixelFormat.Ppm, "RAWMARKER"u8.ToArray(), 1, 1).ToString();
        Assert.DoesNotContain("RAWMARKER", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Sha256_is_lower_case_hex_of_the_png()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ScreenFrame.ComputeSha256("abc"u8.ToArray()));
    }

    // Rate limiter

    [Fact]
    public void Limiter_allows_the_limit_then_refuses_until_the_window_slides()
    {
        var time = new FakeTimeProvider();
        var l = new SlidingWindowRateLimiter(time, 3, TimeSpan.FromMinutes(1));
        Assert.True(l.TryAcquire("a"));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.True(l.TryAcquire("a"));
        Assert.True(l.TryAcquire("a"));
        Assert.False(l.TryAcquire("a"));
        time.Advance(TimeSpan.FromSeconds(41));
        // Only the hit from t=0 has slid out of the window.
        Assert.True(l.TryAcquire("a"));
        Assert.False(l.TryAcquire("a"));
    }

    [Fact]
    public void Limiter_memory_is_bounded_and_evicts_idle_keys_first()
    {
        var time = new FakeTimeProvider();
        var l = new SlidingWindowRateLimiter(time, 5, TimeSpan.FromMinutes(1), maxTrackedKeys: 3);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(l.TryAcquire($"agent-{i}"));
            Assert.True(l.TrackedKeys <= 3);
        }
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(l.TryAcquire("fresh"));
        Assert.Equal(1, l.TrackedKeys);
    }

    [Fact]
    public void Limiter_evicts_the_least_recent_key_when_every_key_is_active()
    {
        var time = new FakeTimeProvider();
        var l = new SlidingWindowRateLimiter(time, 1, TimeSpan.FromMinutes(10), maxTrackedKeys: 2);
        Assert.True(l.TryAcquire("old"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(l.TryAcquire("newer"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(l.TryAcquire("third"));
        Assert.Equal(2, l.TrackedKeys);
        // "newer" is still tracked and limited; "old" was evicted and starts fresh.
        Assert.False(l.TryAcquire("newer"));
        Assert.True(l.TryAcquire("old"));
    }

    [Fact]
    public void Limiter_rejects_bad_arguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(TimeProvider.System, 0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(TimeProvider.System, 1, TimeSpan.Zero));
    }
}
