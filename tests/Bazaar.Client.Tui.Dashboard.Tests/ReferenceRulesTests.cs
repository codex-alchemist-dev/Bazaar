namespace Bazaar.Client.Tui.Dashboard.Tests;

/// <summary>Doc 58: the Dashboard reads only through ports. It never references Bazaar.Server, Bazaar.Client.Core or an implementation module.</summary>
public class ReferenceRulesTests
{
    [Theory]
    [InlineData(typeof(DashboardShell))]
    [InlineData(typeof(OpsLogLine))]
    public void Assemblies_reference_only_abstractions_of_the_server(Type marker)
    {
        var names = marker.Assembly.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty).ToList();

        Assert.DoesNotContain("Bazaar.Server", names);
        Assert.DoesNotContain("Bazaar.Client.Core", names);
        Assert.All(
            names.Where(n => n.StartsWith("Bazaar.Server.", StringComparison.Ordinal)),
            n => Assert.EndsWith(".Abstractions", n));
    }
}
