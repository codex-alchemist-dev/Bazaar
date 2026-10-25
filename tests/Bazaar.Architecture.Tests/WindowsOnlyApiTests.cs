using System.Reflection;

namespace Bazaar.Architecture.Tests;

/// <summary>L0: no Windows-only API outside Bazaar.Server.Providers.Windows (docs/design/03, 34).</summary>
public class WindowsOnlyApiTests
{
    // The platform-neutral projects. Bazaar.Server.Host and the heads are not in this list yet, see the PR notes.
    private static readonly string[] _neutralProjects =
    [
        "Bazaar.Server",
        "Bazaar.Contracts",
        "Bazaar.Sdk",
        "Bazaar.Client.Core",
    ];

    public static TheoryData<Assembly> NeutralAssemblies() =>
    [
        typeof(Server.AssemblyMarker).Assembly,
        typeof(Contracts.AssemblyMarker).Assembly,
        typeof(Sdk.AssemblyMarker).Assembly,
        typeof(Client.Core.App).Assembly,
    ];

    [Theory]
    [MemberData(nameof(NeutralAssemblies))]
    public void Platform_neutral_assembly_references_no_windows_only_library(Assembly assembly) =>
        Assert.Empty(ArchitectureRules.WindowsOnlyReferences(ArchitectureRules.ReferencedNames(assembly)));

    [Fact]
    public void Platform_neutral_source_uses_no_windows_only_api()
    {
        var dirs = _neutralProjects.Select(RepoPaths.SourceDirectory);

        Assert.Empty(ArchitectureRules.WindowsOnlySourceFindings(ArchitectureRules.SourceFiles(dirs)));
    }
}
