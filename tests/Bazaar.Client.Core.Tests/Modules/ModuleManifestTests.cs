using Bazaar.Client.Core.Modules;

namespace Bazaar.Client.Core.Tests.Modules;

public class ModuleManifestTests
{
    private static ModuleManifest Valid() => new(
        Id: "bazaar.gc",
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: ["bazaar.gc"],
        Commands: ["bazaar.gc.open"],
        SettingKeys: ["gc.density"],
        Capabilities: ["wire:gc.history", "storage:local"],
        Isolation: ModuleIsolation.InProcessCompiled);

    [Fact]
    public void Valid_manifest_passes()
    {
        ModuleManifestResult result = ModuleManifestValidator.Validate(Valid());

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => i.Code)));
    }

    [Fact]
    public void Null_manifest_is_rejected()
    {
        Assert.Equal("missing", Assert.Single(ModuleManifestValidator.Validate(null).Issues).Code);
    }

    [Theory]
    [InlineData("GC")]
    [InlineData("gc")]
    [InlineData("bazaar gc")]
    [InlineData("")]
    public void Bad_ids_are_rejected(string id)
    {
        ModuleManifestResult result = ModuleManifestValidator.Validate(Valid() with { Id = id });

        Assert.Contains(result.Issues, i => i.Path == "/id" && i.Code == "bad-id");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("01.0.0")]
    public void Non_semver_versions_are_rejected(string version)
    {
        ModuleManifestResult result = ModuleManifestValidator.Validate(Valid() with { Version = version });

        Assert.Contains(result.Issues, i => i.Code == "bad-version");
    }

    [Fact]
    public void Contract_version_must_be_positive_and_bounded()
    {
        Assert.Contains(ModuleManifestValidator.Validate(Valid() with { ContractVersion = 0 }).Issues, i => i.Code == "bad-contract-version");
        Assert.Contains(ModuleManifestValidator.Validate(Valid() with { ContractVersion = 1001 }).Issues, i => i.Code == "bad-contract-version");
    }

    [Fact]
    public void Unknown_capability_is_rejected_and_duplicates_are_reported()
    {
        ModuleManifestResult unknown = ModuleManifestValidator.Validate(Valid() with { Capabilities = ["wire:everything"] });
        ModuleManifestResult duplicate = ModuleManifestValidator.Validate(Valid() with { Capabilities = ["storage:local", "storage:local"] });

        Assert.Equal("unknown-capability", Assert.Single(unknown.Issues).Code);
        Assert.Equal("duplicate", Assert.Single(duplicate.Issues).Code);
    }

    [Fact]
    public void Bad_pane_command_and_setting_names_are_reported_by_path()
    {
        ModuleManifestResult result = ModuleManifestValidator.Validate(Valid() with
        {
            PaneKinds = ["Bad Pane"],
            Commands = ["nodots"],
            SettingKeys = ["Upper.Case"],
        });

        Assert.Contains(result.Issues, i => i.Path == "/paneKinds/0" && i.Code == "bad-pane-kind");
        Assert.Contains(result.Issues, i => i.Path == "/commands/0" && i.Code == "bad-command");
        Assert.Contains(result.Issues, i => i.Path == "/settingKeys/0" && i.Code == "bad-setting-key");
    }

    [Fact]
    public void Undefined_isolation_value_is_rejected()
    {
        ModuleManifestResult result = ModuleManifestValidator.Validate(Valid() with { Isolation = (ModuleIsolation)99 });

        Assert.Contains(result.Issues, i => i.Path == "/isolation" && i.Code == "bad-isolation");
    }

    [Fact]
    public void Builder_only_registers_what_the_manifest_declares()
    {
        var builder = new ClientModuleBuilder(Valid());

        builder.AddPaneKind("bazaar.gc");
        builder.AddCommand("bazaar.gc.open");

        Assert.Equal(["bazaar.gc"], builder.RegisteredPaneKinds);
        Assert.Equal(["bazaar.gc.open"], builder.RegisteredCommands);
        Assert.Throws<InvalidOperationException>(() => builder.AddPaneKind("bazaar.other"));
        Assert.Throws<InvalidOperationException>(() => builder.AddCommand("bazaar.other.run"));
    }
}
