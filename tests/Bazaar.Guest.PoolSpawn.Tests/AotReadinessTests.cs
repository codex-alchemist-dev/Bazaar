using System.Xml.Linq;

namespace Bazaar.Guest.PoolSpawn.Tests;

/// <summary>The helper must stay publishable as a native AOT binary: the project declares it, and the code pulls in nothing the AOT build would not like.</summary>
public sealed class AotReadinessTests
{
    private static XElement Project()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Bazaar.Guest.PoolSpawn")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return XDocument.Load(Path.Combine(dir.FullName, "src", "Bazaar.Guest.PoolSpawn", "Bazaar.Guest.PoolSpawn.csproj")).Root!;
    }

    private static Dictionary<string, string> UnconditionalProperties() =>
        Project().Elements("PropertyGroup").Where(g => g.Attribute("Condition") is null)
            .SelectMany(g => g.Elements())
            .ToDictionary(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal);

    [Fact]
    public void The_project_declares_it_is_AOT_compatible_and_globalization_invariant()
    {
        var all = UnconditionalProperties();
        Assert.Equal("true", all["IsAotCompatible"]);
        Assert.Equal("true", all["InvariantGlobalization"]);
    }

    [Fact]
    public void Native_AOT_is_on_for_linux_publish_runtimes_only_so_plain_builds_need_no_native_toolchain()
    {
        Assert.False(UnconditionalProperties().ContainsKey("PublishAot"));

        var group = Assert.Single(Project().Elements("PropertyGroup"), g => g.Attribute("Condition") is not null && g.Element("PublishAot") is not null);
        var condition = (string)group.Attribute("Condition")!;
        Assert.Contains("linux-x64", condition, StringComparison.Ordinal);
        Assert.Contains("linux-arm64", condition, StringComparison.Ordinal);
        Assert.Equal("true", group.Element("PublishAot")!.Value.Trim());
        Assert.Equal("Size", group.Element("OptimizationPreference")!.Value.Trim());
        Assert.Equal("false", group.Element("StackTraceSupport")!.Value.Trim());
        // The tool's messages are its own strings, so system resource keys stay off.
        Assert.Null(group.Element("UseSystemResourceKeys"));
    }

    [Fact]
    public void The_assembly_references_only_the_runtime_libraries_it_needs()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.Runtime", "System.Collections", "System.Console", "System.ComponentModel.Primitives", "System.Runtime.InteropServices",
            "System.Private.CoreLib", "Microsoft.Win32.Primitives", "System.Runtime.Extensions", "System.Memory", "System.Linq",
        };
        var referenced = typeof(SpawnArguments).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.NotEmpty(referenced);
        Assert.All(referenced, name => Assert.True(allowed.Contains(name), "unexpected reference: " + name));
    }
}
