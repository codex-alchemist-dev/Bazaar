namespace Bazaar.Architecture.Tests;

/// <summary>
/// Proves each rule fires on a planted violation. Without these, a rule that silently matched nothing
/// would pass every real-tree test.
/// </summary>
public class SeededViolationTests
{
    private static readonly PortabilityConfig _portability = PortabilityConfig.Parse("""
        {
          "candidates": [ { "project": "Bazaar.Rubedo" }, { "project": "Cdax.Core" } ],
          "forbiddenTerms": [ { "term": "AgentId", "ignoreCase": false }, { "term": "GC", "ignoreCase": false }, { "term": "company", "ignoreCase": true } ],
          "namespaceTerms": [ "Bazaar" ],
          "keyPrefixes": [ "bazaar" ],
          "hostReferences": [ "Npgsql", "Bazaar.Server", "Bazaar.Contracts" ],
          "providerTerms": [ "Postgres", "Npgsql", "Vulkan" ],
          "allowlist": [ { "id": "R1", "rule": "P-01", "path": "src/Bazaar.Rubedo", "term": "Bazaar", "reason": "seeded" } ]
        }
        """);

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
    public void Seeded_module_server_reference_is_flagged() =>
        Assert.Equal(["Bazaar.Seeded"], ArchitectureRules.ModuleServerReferences([("Bazaar.Seeded", ["Cdax.Core", "Bazaar.Server"])]));

    [Fact]
    public void Seeded_module_abstractions_server_reference_is_flagged() =>
        Assert.Equal(["Bazaar.Seeded.Abstractions"], ArchitectureRules.ModuleServerReferences([("Bazaar.Seeded.Abstractions", ["Bazaar.Server"])]));

    [Fact]
    public void Seeded_server_module_reference_is_flagged() =>
        Assert.Equal(["Bazaar.Server.Seeded"], ArchitectureRules.ModuleServerReferences([("Bazaar.Server.Seeded", ["Bazaar.Server"])]));

    [Fact]
    public void Seeded_module_without_server_reference_is_not_flagged() =>
        Assert.Empty(ArchitectureRules.ModuleServerReferences([("Bazaar.Seeded", ["Cdax.Core", "Bazaar.Contracts"])]));

    [Fact]
    public void Seeded_exempt_projects_referencing_server_are_not_flagged() =>
        Assert.Empty(ArchitectureRules.ModuleServerReferences(
        [
            ("Bazaar.Server.Host", ["Bazaar.Server"]),
            ("Bazaar.Client.Windows", ["Bazaar.Server"]),
            ("Bazaar.Client.Core", ["Bazaar.Server"]),
            ("Cdax.Core", ["Bazaar.Server"]),
        ]));

    [Fact]
    public void Seeded_non_bazaar_project_is_not_restricted() =>
        Assert.False(ArchitectureRules.IsServerReferenceRestricted("Vendor.Seeded"));

    [Fact]
    public void Repo_root_is_found_from_nested_build_output() =>
        Assert.Equal(RepoPaths.Root, RepoPaths.FindRoot(Path.Combine(RepoPaths.Root, "tests", "Bazaar.Architecture.Tests", "bin")));

    [Fact]
    public void Seeded_forbidden_vocabulary_is_flagged()
    {
        var source = "/// <summary>The AgentId of the GC and its Company.</summary>\npublic sealed class Clean { }\n";

        var terms = ArchitectureRules.VocabularyFindings([("src/Bazaar.Rubedo/Seeded.cs", source)], _portability).Select(f => f.Term).ToList();

        Assert.Equal(["AgentId", "GC", "company"], terms);
    }

    [Fact]
    public void Seeded_product_name_in_namespace_and_public_type_is_flagged()
    {
        var source = "namespace Bazaar.Rubedo;\npublic sealed class BazaarThing { }\ninternal sealed class BazaarHidden { }\n// public class BazaarComment\n";

        var findings = ArchitectureRules.VocabularyFindings([("src/Bazaar.Rubedo/Seeded.cs", source)], _portability).ToList();

        Assert.Equal([1, 2], findings.Select(f => f.Line));
    }

    [Fact]
    public void Seeded_vocabulary_findings_respect_the_allowlist_and_stale_entries_are_reported()
    {
        var hit = new PortabilityFinding("P-01", "src/Bazaar.Rubedo/Views/A.cs", 1, "Bazaar", "namespace Bazaar.Rubedo;");
        var other = new PortabilityFinding("P-01", "src/Bazaar.Kodo/A.cs", 1, "Bazaar", "namespace Bazaar.Kodo;");

        Assert.Equal([other], ArchitectureRules.Unallowed([hit, other], _portability));
        Assert.Single(ArchitectureRules.StaleEntries([other], _portability));
        Assert.Empty(ArchitectureRules.StaleEntries([hit], _portability));
    }

    [Fact]
    public void Seeded_host_prefixed_keys_and_files_are_flagged()
    {
        var source = "const string a = \"bazaar.audit.v1\";\nconst string b = \"BAZAAR_TEST_SEED\";\nconst string c = \"cdax.audit.v1\";\n// \"bazaar.in.comment\"\n";

        var findings = ArchitectureRules.HostPrefixFindings([("src/Bazaar.Rubedo/BazaarFile.cs", source)], _portability).ToList();

        Assert.Equal([0, 1, 2], findings.Select(f => f.Line));
    }

    [Fact]
    public void Seeded_package_and_foreign_project_references_are_flagged()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
                <ProjectReference Include="..\Cdax.Core\Cdax.Core.csproj" />
                <ProjectReference Include="..\Other.Lib\Other.Lib.csproj" />
                <ProjectReference Include="..\Bazaar.Contracts\Bazaar.Contracts.csproj" />
              </ItemGroup>
            </Project>
            """;

        var terms = ArchitectureRules.PackageReferenceFindings([("src/Bazaar.Rubedo/Bazaar.Rubedo.csproj", csproj)], _portability).Select(f => f.Term);

        Assert.Equal(["Newtonsoft.Json", "Other.Lib"], terms);
    }

    [Fact]
    public void Seeded_host_assumptions_are_flagged()
    {
        var csproj = "<Project><ItemGroup><PackageReference Include=\"Npgsql\" /><ProjectReference Include=\"..\\Bazaar.Server\\Bazaar.Server.csproj\" /></ItemGroup></Project>";
        var source = "using Npgsql;\nusing Bazaar.Contracts.Kodo;\nvar c = new NpgsqlConnection();\nvar ok = new MyNpgsqlThing();\n";

        var findings = ArchitectureRules.HostAssumptionFindings(
            [("src/Bazaar.Rubedo/Bazaar.Rubedo.csproj", csproj)], [("src/Bazaar.Rubedo/A.cs", source)], _portability).ToList();

        Assert.Equal(["Npgsql", "Bazaar.Server", "Npgsql", "Bazaar.Contracts", "Npgsql"], findings.Select(f => f.Term));
    }

    [Fact]
    public void Seeded_provider_references_and_folders_are_flagged()
    {
        var csproj = "<Project><ItemGroup><PackageReference Include=\"Testcontainers.Postgres\" /><PackageReference Include=\"Vortice.Vulkan\" /><PackageReference Include=\"Fine.Lib\" /></ItemGroup></Project>";

        var findings = ArchitectureRules.ProviderFindings(
            [("src/Bazaar.Rubedo/Bazaar.Rubedo.csproj", csproj)],
            [("src/Bazaar.Rubedo/Postgres/Store.cs", string.Empty), ("src/Bazaar.Rubedo/Core/Fine.cs", string.Empty)],
            _portability).ToList();

        Assert.Equal(["Testcontainers.Postgres", "Vortice.Vulkan", "Postgres"], findings.Select(f => f.Term));
    }

    [Fact]
    public void Seeded_clean_portable_source_is_not_flagged()
    {
        var source = "using System;\nnamespace Cdax.Rubedo;\npublic sealed class Engine { }\n";
        var csproj = "<Project Sdk=\"Microsoft.NET.Sdk\" />";
        var sources = new[] { ("src/Cdax.Core/Engine.cs", source) };
        var projects = new[] { ("src/Cdax.Core/Cdax.Core.csproj", csproj) };

        Assert.Empty(ArchitectureRules.VocabularyFindings(sources, _portability));
        Assert.Empty(ArchitectureRules.HostPrefixFindings(sources, _portability));
        Assert.Empty(ArchitectureRules.PackageReferenceFindings(projects, _portability));
        Assert.Empty(ArchitectureRules.HostAssumptionFindings(projects, sources, _portability));
        Assert.Empty(ArchitectureRules.ProviderFindings(projects, sources, _portability));
    }

    [Fact]
    public void Missing_candidate_projects_are_skipped_not_failures()
    {
        var config = _portability with { Candidates = [new PortabilityCandidate("Cdax.Does.Not.Exist")] };

        Assert.Empty(config.PresentProjects());
        Assert.Equal(["Cdax.Does.Not.Exist"], config.MissingProjects());
        Assert.Empty(ArchitectureRules.PortabilityFiles(config.PresentProjects(), "*.cs"));
    }
}
