using Bazaar.Contracts.Modules;
using Bazaar.Server.Providers;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class WindowsSandboxHostFactsTests
{
    private const string _systemDirectory = @"C:\Windows\System32";

    [Fact]
    public void The_sandbox_feature_is_reported_only_when_its_executable_exists()
    {
        var expected = Path.Combine(_systemDirectory, WindowsSandboxHostFacts.SandboxExecutable);
        var facts = new WindowsSandboxHostFacts(path => path == expected, () => _systemDirectory);

        var features = facts.Current().Features;

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("windows-sandbox", features);
        }
        else
        {
            Assert.DoesNotContain("windows-sandbox", features);
        }
    }

    [Fact]
    public void A_missing_executable_fails_closed_with_no_sandbox_feature()
    {
        var facts = new WindowsSandboxHostFacts(_ => false, () => _systemDirectory);

        Assert.DoesNotContain("windows-sandbox", facts.Current().Features);
    }

    [Fact]
    public void The_probe_asks_for_the_executable_under_the_system_directory()
    {
        string? asked = null;
        var facts = new WindowsSandboxHostFacts(path => { asked = path; return false; }, () => _systemDirectory);

        _ = facts.Current();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(_systemDirectory, WindowsSandboxHostFacts.SandboxExecutable), asked);
        }
        else
        {
            Assert.Null(asked);
        }
    }

    [Fact]
    public void The_sandbox_never_fits_the_agent_pair_even_when_its_feature_is_present()
    {
        var facts = new FixedHostFacts(new HostFacts("windows", "x64", new HashSet<string>(StringComparer.Ordinal) { "windows-sandbox" }));
        using var backend = new WindowsSandboxBackend(new FakeWindowsSandboxHost(), facts, new WindowsSandboxOptions(Guid.NewGuid()));

        var selection = ProviderSelector.Select(ProviderRole.Vm, ProviderSelector.Auto, new IVmProvider[] { backend }, ProviderRequirements.AgentPairConcurrency);

        Assert.Null(selection.Provider);
    }
}
