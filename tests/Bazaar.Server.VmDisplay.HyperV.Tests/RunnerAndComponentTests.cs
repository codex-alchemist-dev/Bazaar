using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDisplay.HyperV.Tests;

public class RunnerAndComponentTests
{
    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<ProcessSpec> Specs { get; } = [];

        public ProcessResult Result { get; set; } = new(0, "out", string.Empty);

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            return Task.FromResult(Result);
        }
    }

    [Fact]
    public async Task The_runner_sends_the_constant_script_on_stdin_and_the_data_in_the_environment()
    {
        var launcher = new FakeLauncher();
        var runner = new ProcessHyperVScriptRunner(new HyperVOptions { PowerShellPath = "pwsh", CommandTimeoutSeconds = 9 }, launcher);
        const string hostile = "x'; calc $(Get-Date) `n \"";

        string output = await runner.RunAsync(
            HyperVScripts.Keys,
            new Dictionary<string, string> { ["Name"] = "vm1", ["Steps"] = "[{\"op\":\"type\",\"text\":\"" + hostile.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}]" },
            CancellationToken.None);

        Assert.Equal("out", output);
        ProcessSpec spec = Assert.Single(launcher.Specs);
        Assert.Equal("pwsh", spec.FileName);
        Assert.Equal(["-NoProfile", "-NonInteractive", "-Command", "-"], spec.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(9), spec.Timeout);

        // The script text is exactly the constant, so no value is part of it, and no value is on the command line.
        Assert.Equal(HyperVScripts.Keys + "\n", spec.StdIn);
        Assert.DoesNotContain("calc", spec.StdIn, StringComparison.Ordinal);
        Assert.DoesNotContain(spec.Arguments, a => a.Contains("vm1", StringComparison.Ordinal) || a.Contains("calc", StringComparison.Ordinal));

        string json = spec.Environment[ProcessHyperVScriptRunner.ParametersVariable];
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("vm1", doc.RootElement.GetProperty("Name").GetString());
        Assert.Contains("calc", doc.RootElement.GetProperty("Steps").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Parameter_json_escapes_non_ascii_and_control_characters()
    {
        string json = ProcessHyperVScriptRunner.ParametersJson(new Dictionary<string, string> { ["A"] = "café\n\"" });
        Assert.All(json, c => Assert.True(c is >= ' ' and <= '~', $"Unescaped character U+{(int)c:X4}"));
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal("café\n\"", doc.RootElement.GetProperty("A").GetString());
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_source_failure_with_trimmed_stderr()
    {
        var launcher = new FakeLauncher { Result = new ProcessResult(1, string.Empty, new string('e', 500)) };
        var runner = new ProcessHyperVScriptRunner(new HyperVOptions(), launcher);
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => runner.RunAsync(HyperVScripts.Mouse, new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.True(e.Message.Length < 300);
    }

    [Fact]
    public void Options_bind_defaults_and_reject_bad_sizes()
    {
        HyperVOptions d = HyperVOptions.Bind(new ConfigurationBuilder().Build());
        Assert.True(d.Enabled);
        Assert.Equal("powershell.exe", d.PowerShellPath);
        Assert.Equal((1280, 800, 15), (d.ThumbnailWidth, d.ThumbnailHeight, d.CommandTimeoutSeconds));

        IConfiguration bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:VmDisplay:HyperV:ThumbnailWidth"] = "70000" }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => HyperVOptions.Bind(bad));
    }

    [Fact]
    public void Manifest_and_registration_match_the_brief()
    {
        var component = new HyperVComponent();
        Assert.Equal("vm-display-hyperv", component.Manifest.Id);
        Assert.Equal(ComponentKind.Feature, component.Manifest.Kind);
        Assert.Equal(["vm-display"], component.Manifest.DependsOn);

        var services = new ServiceCollection();
        services.AddSingleton<IHypervisorConsole>(new OtherConsole());
        component.AddServices(services, new ConfigurationBuilder().Build());
        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Equal(2, sp.GetServices<IHypervisorConsole>().Count());
        Assert.Contains(sp.GetServices<IHypervisorConsole>(), c => c is HyperVConsole);
        Assert.IsType<ProcessHyperVScriptRunner>(sp.GetRequiredService<IHyperVScriptRunner>());
    }

    [Fact]
    public void A_registered_runner_is_not_replaced_by_the_default()
    {
        var services = new ServiceCollection();
        var mine = new FakeRunner();
        services.AddSingleton<IHyperVScriptRunner>(mine);
        new HyperVComponent().AddServices(services, new ConfigurationBuilder().Build());
        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.Same(mine, sp.GetRequiredService<IHyperVScriptRunner>());
    }

    private sealed class OtherConsole : IHypervisorConsole
    {
        public bool Supports(DisplayTarget target) => false;

        public Task<RawFrame> ReadFramebufferAsync(DisplayTarget target, int maxWidth, int maxHeight, CancellationToken ct) => throw new NotSupportedException();

        public Task SendInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct) => throw new NotSupportedException();
    }
}
