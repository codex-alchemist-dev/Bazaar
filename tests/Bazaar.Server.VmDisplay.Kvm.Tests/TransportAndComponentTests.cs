using Bazaar.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.Kvm.Tests;

public class TransportAndComponentTests
{
    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<(string File, string[] Args, TimeSpan Timeout)> Calls { get; } = [];

        public ProcessResult Result { get; set; } = new(0, "{\"return\":{}}", string.Empty);

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, int maxStdoutChars, CancellationToken ct)
        {
            Calls.Add((fileName, [.. arguments], timeout));
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task Virsh_argv_is_exact_and_each_value_is_one_argument()
    {
        var launcher = new FakeLauncher();
        var transport = new VirshQmpTransport(new KvmOptions { LibvirtUri = "qemu+ssh://host/system", VirshPath = "/usr/bin/virsh", CommandTimeoutSeconds = 7 }, launcher);
        string json = "{\"execute\":\"query-status\"}";

        string response = await transport.ExecuteAsync("agent-vm.1", json, CancellationToken.None);

        Assert.Equal("{\"return\":{}}", response);
        (string file, string[] args, TimeSpan timeout) = Assert.Single(launcher.Calls);
        Assert.Equal("/usr/bin/virsh", file);
        Assert.Equal(["-c", "qemu+ssh://host/system", "qemu-monitor-command", "agent-vm.1", json], args);
        Assert.Equal(TimeSpan.FromSeconds(7), timeout);
    }

    [Fact]
    public async Task Virsh_rejects_bad_names_and_non_zero_exit()
    {
        var launcher = new FakeLauncher();
        var transport = new VirshQmpTransport(new KvmOptions(), launcher);
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => transport.ExecuteAsync("a b; c", "{}", CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(launcher.Calls);

        launcher.Result = new ProcessResult(1, string.Empty, "error: failed to get domain");
        e = await Assert.ThrowsAsync<DisplayException>(() => transport.ExecuteAsync("vm1", "{}", CancellationToken.None));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Contains("failed to get domain", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_bind_defaults_and_overrides()
    {
        KvmOptions d = KvmOptions.Bind(new ConfigurationBuilder().Build());
        Assert.True(d.Enabled);
        Assert.Equal("qemu:///system", d.LibvirtUri);
        Assert.Equal("virsh", d.VirshPath);
        Assert.Equal("/run/bazaar/display", d.ScreendumpDirectory);
        Assert.Equal(10, d.CommandTimeoutSeconds);

        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:VmDisplay:Kvm:Enabled"] = "false",
            ["Bazaar:VmDisplay:Kvm:LibvirtUri"] = "qemu:///session",
            ["Bazaar:VmDisplay:Kvm:CommandTimeoutSeconds"] = "3",
        }).Build();
        KvmOptions o = KvmOptions.Bind(config);
        Assert.False(o.Enabled);
        Assert.Equal("qemu:///session", o.LibvirtUri);
        Assert.Equal(3, o.CommandTimeoutSeconds);

        IConfiguration bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:VmDisplay:Kvm:CommandTimeoutSeconds"] = "0" }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => KvmOptions.Bind(bad));
    }

    [Fact]
    public void Manifest_and_registration_match_the_brief()
    {
        var component = new KvmComponent();
        Assert.Equal("vm-display-kvm", component.Manifest.Id);
        Assert.Equal(ComponentKind.Feature, component.Manifest.Kind);
        Assert.Equal(["vm-display"], component.Manifest.DependsOn);

        var services = new ServiceCollection();
        services.AddSingleton<IHypervisorConsole>(new OtherConsole());
        component.AddServices(services, new ConfigurationBuilder().Build());
        using ServiceProvider sp = services.BuildServiceProvider();
        IHypervisorConsole[] consoles = [.. sp.GetServices<IHypervisorConsole>()];
        Assert.Equal(2, consoles.Length);
        Assert.Contains(consoles, c => c is KvmConsole);
        Assert.IsType<VirshQmpTransport>(sp.GetRequiredService<IQmpTransport>());
        Assert.IsType<LocalScreendumpFileReader>(sp.GetRequiredService<IScreendumpFileReader>());
    }

    [Fact]
    public void A_registered_transport_is_not_replaced_by_the_default()
    {
        var services = new ServiceCollection();
        var mine = new FakeTransport();
        services.AddSingleton<IQmpTransport>(mine);
        new KvmComponent().AddServices(services, new ConfigurationBuilder().Build());
        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Same(mine, sp.GetRequiredService<IQmpTransport>());
    }

    [Fact]
    public async Task The_local_reader_caps_size_and_delete_is_quiet_for_missing_files()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[100], TestContext.Current.CancellationToken);
            Assert.Equal(100, (await new LocalScreendumpFileReader(100).ReadAsync(path, CancellationToken.None)).Length);
            DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => new LocalScreendumpFileReader(99).ReadAsync(path, CancellationToken.None));
            Assert.Equal(DisplayFailure.FrameTooLarge, e.Failure);
        }
        finally
        {
            var reader = new LocalScreendumpFileReader(100);
            reader.Delete(path);
            reader.Delete(path);
        }

        Assert.False(File.Exists(path));
    }

    private sealed class OtherConsole : IHypervisorConsole
    {
        public bool Supports(DisplayTarget target) => false;

        public Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct) => throw new NotSupportedException();

        public Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct) => throw new NotSupportedException();
    }
}
