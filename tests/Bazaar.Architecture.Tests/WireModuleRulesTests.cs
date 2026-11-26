using System.Xml.Linq;

namespace Bazaar.Architecture.Tests;

/// <summary>
/// Doc 90 dependency rules for the wire modules: the Protobuf tooling (the one place the old no-Protobuf rule was lifted) stays
/// in its own codec module, and Contracts stays package-free.
/// </summary>
public class WireModuleRulesTests
{
    private static readonly string[] _protobufPackages = ["Google.Protobuf", "Grpc.Tools", "Grpc.Net.Client", "Grpc.AspNetCore", "Grpc.AspNetCore.Web"];

    [Fact]
    public void Only_the_protobuf_and_grpc_modules_reference_protobuf_packages()
    {
        var offenders = new List<string>();
        foreach (var project in Directory.EnumerateFiles(RepoPaths.Root, "*.csproj", SearchOption.AllDirectories))
        {
            if (project.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || project.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || project.Contains($"{Path.DirectorySeparatorChar}spikes{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(project);
            var allowed = name is "Bazaar.Wire.Codec.Protobuf" || name.Contains("Transport.Grpc", StringComparison.Ordinal) || name.StartsWith("Bazaar.Wire.Codec.Protobuf.", StringComparison.Ordinal);
            var references = XDocument.Load(project).Descendants("PackageReference").Select(e => (string?)e.Attribute("Include"));
            if (!allowed && references.Any(r => r is not null && _protobufPackages.Contains(r))) offenders.Add(name);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Contracts_has_no_package_references()
    {
        var references = XDocument.Load(RepoPaths.ProjectFile("Bazaar.Contracts")).Descendants("PackageReference");

        Assert.Empty(references);
    }
}
