#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Mnemonics;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Calcinato.Tests.Mnemonics;

public sealed class CalcinatoModuleTests
{
    [Fact]
    public void ManifestPassesTheSharedValidator()
    {
        var result = ModuleManifestValidator.Validate(new CalcinatoModule().Manifest);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void ManifestDeclaresTheFourCommandsAndOnlyTheProcessClass()
    {
        var manifest = new CalcinatoModule().Manifest;

        Assert.Equal(CalcinatoCommands.CommandIds, manifest.Commands);
        Assert.Equal(new[] { "calcinato.terminal", "calcinato.newterm", "calcinato.attach", "calcinato.intr" }, manifest.Commands);
        Assert.Equal(new[] { "process:spawn" }, manifest.Capabilities);
        Assert.Equal(ModuleIsolation.InProcessCompiled, manifest.Isolation);
        Assert.Empty(manifest.PaneKinds);
    }

    [Fact]
    public void ManifestNamesNoHostsOutsideWindowsAndLinux()
    {
        var host = new CalcinatoModule().Manifest.Host;

        Assert.Equal(new[] { "windows", "linux" }, host.OperatingSystems);
        Assert.Empty(host.RequiredFeatures);
    }

    [Fact]
    public void RegisterAddsEveryManifestCommandToTheBuilder()
    {
        var module = new CalcinatoModule();
        var builder = new ClientModuleBuilder(module.Manifest);

        module.Register(builder);

        Assert.Equal(module.Manifest.Commands.OrderBy(c => c, StringComparer.Ordinal), builder.RegisteredCommands.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void ModuleIdIsDottedBecauseTheValidatorRequiresIt()
    {
        var bare = new CalcinatoModule().Manifest with { Id = "calcinato" };

        var result = ModuleManifestValidator.Validate(bare);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "bad-id");
    }

    [Fact]
    public void ManifestWithAnUnknownCapabilityIsRejected()
    {
        var widened = new CalcinatoModule().Manifest with { Capabilities = ["process:spawn", "network:remote-attach"] };

        var result = ModuleManifestValidator.Validate(widened);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "unknown-capability");
    }

    [Fact]
    public void CommandIdsAreDottedNames()
    {
        var result = ModuleManifestValidator.Validate(new CalcinatoModule().Manifest);

        Assert.True(result.IsValid);
        Assert.All(CalcinatoCommands.CommandIds, id => Assert.Contains('.', id));
    }
}
