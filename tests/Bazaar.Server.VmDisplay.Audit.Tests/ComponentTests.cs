using Cdax.Core;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.Audit.Tests;

public class ComponentTests
{
    private sealed class RecordingLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private sealed class AllowGate : IDisplayGate
    {
        public ValueTask<DisplayVerdict> CheckCaptureAsync(DisplayTarget target, CancellationToken ct) => ValueTask.FromResult(DisplayVerdict.Allow);

        public ValueTask<DisplayVerdict> CheckInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct) => ValueTask.FromResult(DisplayVerdict.Allow);
    }

    private sealed class FakeConsole : IHypervisorConsole
    {
        public List<IReadOnlyList<InputAction>> Inputs { get; } = [];

        public bool Supports(DisplayTarget target) => true;

        public Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct) =>
            Task.FromResult(new RawFrame(RawPixelFormat.Bgra32, new byte[4 * 4 * 4], 4, 4));

        public Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
        {
            Inputs.Add(actions);
            return Task.CompletedTask;
        }
    }

    private static readonly DisplayTarget _target = new("agent-a", Guid.NewGuid(), DisplayGuestOs.Windows, "backend-x");

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(bool auditFirst, Action<IServiceCollection>? host = null, IConfiguration? config = null)
    {
        var services = new ServiceCollection();
        config ??= Config();
        if (auditFirst)
        {
            new VmDisplayAuditComponent().AddServices(services, config);
            new VmDisplayComponent().AddServices(services, config);
        }
        else
        {
            new VmDisplayComponent().AddServices(services, config);
            new VmDisplayAuditComponent().AddServices(services, config);
        }

        host?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_is_as_designed()
    {
        ComponentManifest m = new VmDisplayAuditComponent().Manifest;

        Assert.Equal("vm-display-audit", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["vm-display"], m.DependsOn);
        Assert.Equal(["vm-display:audit"], m.Provides);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_sink_replaces_the_no_op_default_in_either_order(bool auditFirst)
    {
        using ServiceProvider sp = Build(auditFirst, s => s.AddSingleton<IAuditLog>(new RecordingLog()));

        Assert.IsType<AuditLogDisplayAuditSink>(sp.GetRequiredService<IDisplayAuditSink>());
        Assert.Single(sp.GetServices<IDisplayAuditSink>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_gate_stays_the_unconfigured_refusing_default(bool auditFirst)
    {
        using ServiceProvider sp = Build(auditFirst, s => s.AddSingleton<IAuditLog>(new RecordingLog()));

        IDisplayGate gate = sp.GetRequiredService<IDisplayGate>();

        Assert.False((await gate.CheckCaptureAsync(_target, TestContext.Current.CancellationToken)).Allowed);
        Assert.False((await gate.CheckInputAsync(_target, [new KeyStroke("Tab")], TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public void A_later_host_registration_still_wins()
    {
        var custom = new RecordingSink();

        using ServiceProvider sp = Build(auditFirst: false, s => s.AddSingleton<IDisplayAuditSink>(custom));

        Assert.Same(custom, sp.GetRequiredService<IDisplayAuditSink>());
    }

    [Fact]
    public async Task A_refused_call_through_the_real_service_writes_a_refusal_row()
    {
        var log = new RecordingLog();
        using ServiceProvider sp = Build(auditFirst: true, s => s.AddSingleton<IAuditLog>(log));

        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() =>
            sp.GetRequiredService<IGuestDisplay>().CaptureAsync(new CaptureRequest(_target), TestContext.Current.CancellationToken));

        Assert.Equal(DisplayFailure.Refused, e.Failure);
        AuditEntry row = Assert.Single(log.Entries);
        Assert.Equal(AuditLogDisplayAuditSink.RefusedKind, row.Kind);
        Assert.Contains("no display gate configured", row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_and_input_through_the_real_service_write_rows_without_pixels_or_text()
    {
        var log = new RecordingLog();
        var console = new FakeConsole();
        using ServiceProvider sp = Build(auditFirst: false, s =>
        {
            s.AddSingleton<IAuditLog>(log);
            s.AddSingleton<IDisplayGate>(new AllowGate());
            s.AddSingleton<IHypervisorConsole>(console);
        });
        IGuestDisplay display = sp.GetRequiredService<IGuestDisplay>();
        const string typed = "typed-text-marker-31bd";

        ScreenFrame frame = await display.CaptureAsync(new CaptureRequest(_target), TestContext.Current.CancellationToken);
        await display.SendInputAsync(new InputBatch(_target, [new TypeText(typed), new KeyStroke("Enter")]), TestContext.Current.CancellationToken);

        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(AuditLogDisplayAuditSink.CapturedKind, log.Entries[0].Kind);
        Assert.Contains(frame.Sha256, log.Entries[0].Payload, StringComparison.Ordinal);
        Assert.Equal(AuditLogDisplayAuditSink.InputSentKind, log.Entries[1].Kind);
        Assert.Contains("\"action_count\":2", log.Entries[1].Payload, StringComparison.Ordinal);
        Assert.Single(console.Inputs);
        foreach (AuditEntry entry in log.Entries)
        {
            Assert.DoesNotContain(typed, entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(frame.Png), entry.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("backend-x", entry.Payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Without_an_audit_log_a_capture_is_not_returned()
    {
        using ServiceProvider sp = Build(auditFirst: true, s =>
        {
            s.AddSingleton<IDisplayGate>(new AllowGate());
            s.AddSingleton<IHypervisorConsole>(new FakeConsole());
        });

        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() =>
            sp.GetRequiredService<IGuestDisplay>().CaptureAsync(new CaptureRequest(_target), TestContext.Current.CancellationToken));

        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
    }

    [Fact]
    public void Bad_options_fail_at_start()
    {
        Assert.Throws<InvalidOperationException>(() => Build(true, config: Config(("Bazaar:VmDisplayAudit:WriteTimeoutSeconds", "0"))));
        Assert.Throws<InvalidOperationException>(() => Build(true, config: Config(("Bazaar:VmDisplayAudit:MaxReasonLength", "5000"))));
    }

    [Fact]
    public void Options_bind_from_the_section()
    {
        DisplayAuditOptions o = DisplayAuditOptions.Bind(Config(("Bazaar:VmDisplayAudit:WriteTimeoutSeconds", "9"), ("Bazaar:VmDisplayAudit:MaxReasonLength", "50")));

        Assert.Equal(9, o.WriteTimeoutSeconds);
        Assert.Equal(50, o.MaxReasonLength);
        Assert.Equal(5, new DisplayAuditOptions().WriteTimeoutSeconds);
    }

    private sealed class RecordingSink : IDisplayAuditSink
    {
        public void Captured(DisplayTarget target, DisplaySourceKind source, int width, int height, int bytes, string sha256)
        {
        }

        public void InputSent(DisplayTarget target, DisplaySourceKind source, int actionCount)
        {
        }

        public void Refused(DisplayTarget target, string operation, string reason)
        {
        }
    }
}
