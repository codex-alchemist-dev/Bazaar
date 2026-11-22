using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Windowing.Tests;

/// <summary>The module's manifest and registration. Uses the real <see cref="ClientModuleBuilder"/>.</summary>
public sealed class WindowingModuleTests
{
    [Fact]
    public void Manifest_passes_contract_validation()
    {
        var result = ModuleManifestValidator.Validate(new WindowingModule().Manifest);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Manifest_identity_and_isolation_match_the_plan()
    {
        var manifest = new WindowingModule().Manifest;

        Assert.Equal("bazaar.client.windowing", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(1, manifest.ContractVersion);
        Assert.Equal(ModuleIsolation.InProcessCompiled, manifest.Isolation);
    }

    [Fact]
    public void Manifest_declares_no_pane_kinds_and_the_ui_pane_capability()
    {
        var manifest = new WindowingModule().Manifest;

        Assert.Empty(manifest.PaneKinds);
        Assert.Equal(["ui:pane"], manifest.Capabilities);
    }

    [Fact]
    public void Manifest_declares_the_four_commands_and_the_setting()
    {
        var manifest = new WindowingModule().Manifest;

        Assert.Equal(
            ["window.detach", "window.dock", "window.new-editor", "window.new-terminal"],
            manifest.Commands);
        Assert.Equal(["windows.startup.budget_detached_window_ms"], manifest.SettingKeys);
    }

    [Fact]
    public void Host_requirements_name_windows_and_linux_on_x64_and_arm64()
    {
        var host = new WindowingModule().Manifest.Host;

        Assert.Equal(HostRequirements.CurrentSchemaVersion, host.SchemaVersion);
        Assert.Equal(["windows", "linux"], host.OperatingSystems);
        Assert.Equal(["x64", "arm64"], host.Architectures);
        Assert.Empty(host.RequiredFeatures);
        Assert.Empty(HostCompatibility.ValidateRequirements(host));
    }

    [Fact]
    public void Static_manifest_matches_the_instance_manifest()
    {
        Assert.Same(WindowingModule.DeclaredManifest, new WindowingModule().Manifest);
    }

    [Fact]
    public void Module_is_an_IClientModule()
    {
        var module = Assert.IsAssignableFrom<IClientModule>(new WindowingModule());

        Assert.Equal("bazaar.client.windowing", module.Manifest.Id);
    }

    [Fact]
    public void Register_records_exactly_the_four_commands()
    {
        var builder = new ClientModuleBuilder(new WindowingModule().Manifest);

        new WindowingModule().Register(builder);

        Assert.Equal(
            ["window.detach", "window.dock", "window.new-editor", "window.new-terminal"],
            builder.RegisteredCommands.OrderBy(c => c, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Register_records_no_pane_kinds()
    {
        var builder = new ClientModuleBuilder(new WindowingModule().Manifest);

        new WindowingModule().Register(builder);

        Assert.Empty(builder.RegisteredPaneKinds);
    }

    [Fact]
    public void Register_rejects_a_null_builder()
    {
        Assert.Throws<ArgumentNullException>(() => new WindowingModule().Register(null!));
    }
}
