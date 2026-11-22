using Bazaar.Contracts.Modules;
using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// Build-time check of client module host requirements (docs/design/41 section 8). The client host skips a module whose
/// requirements the host does not meet, so the build reports those modules per target instead of failing the build.
/// </summary>
public static class CompanyBuildHostCheck
{
    /// <summary>The host facts a target's runtime identifier stands for, or null when the identifier is missing or unknown.</summary>
    public static HostFacts? FactsFor(CompanyBuildTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var parts = target.RuntimeIdentifier?.Split('-');
        if (parts is not [var os, var architecture])
        {
            return null;
        }

        var hostOs = os switch
        {
            "win" => "windows",
            "linux" => "linux",
            "android" => "android",
            _ => null,
        };
        return hostOs is null || !HostCompatibility.KnownArchitectures.Contains(architecture)
            ? null
            : new HostFacts(hostOs, architecture, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Lists each module the target cannot host, as <c>id (reason)</c>. A target without known facts lists nothing.</summary>
    public static IReadOnlyList<string> Unsupported(IEnumerable<ModuleManifest> modules, CompanyBuildTarget target)
    {
        ArgumentNullException.ThrowIfNull(modules);

        var facts = FactsFor(target);
        if (facts is null)
        {
            return [];
        }

        // Features (hyperv, kvm) are server or runtime facts, not build facts: only OS and architecture are judged here.
        return modules
            .Select(m => (m.Id, Issues: HostCompatibility.Check(m.Host, facts).Where(i => i.Code is "os-unsupported" or "arch-unsupported").ToList()))
            .Where(m => m.Issues.Count > 0)
            .Select(m => $"{m.Id} ({string.Join(", ", m.Issues.Select(i => i.Code))})")
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
