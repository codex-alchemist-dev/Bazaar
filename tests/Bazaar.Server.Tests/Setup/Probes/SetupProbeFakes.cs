using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Probes;

namespace Bazaar.Server.Tests.Setup.Probes;

internal sealed class FakeHostFacts : IHostFacts
{
    public bool IsWindows { get; set; }

    public bool IsLinux { get; set; } = true;

    public bool Is64Bit { get; set; } = true;

    public string DefaultDataRoot { get; set; } = "/var/lib/bazaar";

    public HashSet<string> ExistingDirectories { get; } = [];

    public HashSet<string> ReadOnlyDirectories { get; } = [];

    public List<int> Listening { get; } = [];

    public string? Volume { get; set; } = "/";

    public bool DirectoryExists(string path) => ExistingDirectories.Contains(path);

    public bool IsMarkedReadOnly(string path) => ReadOnlyDirectories.Contains(path);

    public IReadOnlyCollection<int> ListeningTcpPorts() => Listening;

    public string? VolumeOf(string path) => Volume;
}

internal sealed class FakeReachability(Func<Uri, bool> answer) : IReachabilityClient
{
    public List<Uri> Asked { get; } = [];

    public Task<bool> IsReachableAsync(Uri url, TimeSpan limit, CancellationToken cancellationToken)
    {
        Asked.Add(url);
        return Task.FromResult(answer(url));
    }
}

internal static class ProbeTestPlans
{
    public static SetupVerificationContext Context(SetupPlan? plan = null) => new(plan ?? new SetupPlan());

    public static string AllText(SetupProbeReading reading) => reading.Message + " " + reading.Remediation;
}
