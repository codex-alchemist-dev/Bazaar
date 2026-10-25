namespace Bazaar.Architecture.Tests;

/// <summary>
/// Proves each rule fires on a planted violation. Without these, a rule that silently matched nothing
/// would pass every real-tree test.
/// </summary>
public class SeededViolationTests
{
    [Fact]
    public void Seeded_windows_registry_reference_is_flagged() =>
        Assert.Single(ArchitectureRules.WindowsOnlyReferences(["System.Runtime", "Microsoft.Win32.Registry"]));

    [Fact]
    public void Seeded_windows_source_is_flagged()
    {
        var source = """
            using System;
            using Microsoft.Win32;

            [assembly: System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041")]
            """;

        var findings = ArchitectureRules.WindowsOnlySourceFindings([("src/Bazaar.Server/Seeded.cs", source)]).ToList();

        Assert.Equal(2, findings.Count);
        Assert.StartsWith("src/Bazaar.Server/Seeded.cs:2:", findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Seeded_clean_source_is_not_flagged() =>
        Assert.Empty(ArchitectureRules.WindowsOnlySourceFindings([("src/Bazaar.Server/Clean.cs", "using System;\n")]));

    [Fact]
    public void Seeded_client_head_reference_in_core_is_flagged() =>
        Assert.Equal(["Bazaar.Client.Windows"], ArchitectureRules.ClientHeadReferences(["Avalonia", "Bazaar.Client.Windows", "Bazaar.Client.Core"]));

    [Fact]
    public void Seeded_server_reference_in_contracts_is_flagged() =>
        Assert.Equal(["Bazaar.Server.Host"], ArchitectureRules.ServerReferences(["Bazaar.Server.Host"]));

    [Fact]
    public void Seeded_declared_server_project_reference_is_flagged()
    {
        var csproj = Path.Combine(Path.GetTempPath(), "bazaar-seeded-" + Guid.NewGuid().ToString("N") + ".csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Bazaar.Server\Bazaar.Server.csproj" />
              </ItemGroup>
            </Project>
            """);
        try
        {
            Assert.Equal(["Bazaar.Server"], ArchitectureRules.ServerReferences(ArchitectureRules.ProjectReferenceNames(csproj)));
        }
        finally
        {
            File.Delete(csproj);
        }
    }

    [Fact]
    public void Repo_root_is_found_from_nested_build_output() =>
        Assert.Equal(RepoPaths.Root, RepoPaths.FindRoot(Path.Combine(RepoPaths.Root, "tests", "Bazaar.Architecture.Tests", "bin")));
}
