using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

internal sealed class FixedHostFacts(HostFacts facts) : IHostFactsSource
{
    public HostFacts Current() => facts;

    public static HostFacts WindowsWithSandbox() =>
        new("windows", "x64", new HashSet<string>(["windows-sandbox"], StringComparer.Ordinal));
}
