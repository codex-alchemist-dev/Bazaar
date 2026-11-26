using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup;

/// <summary>The plan's <c>storage</c> section (doc 63 section 10): shape checks, and that a plan asking for a volume asks for the section.</summary>
public sealed class SetupPlanStorageTests
{
    private const string _database = "\"database\": { \"postgresMajor\": 18, \"dataRoot\": \"C:\\\\ProgramData\\\\Bazaar\", \"gcPartitionCount\": 16 },";

    private static SetupLoadResult Load(string storage, string? database = null) =>
        SetupPlanLoader.Load(
            SetupFixtures.With(_database, (database ?? _database) + " \"storage\": " + storage + ","),
            RegistrySetupSettingCatalog.Known);

    private static bool HasError(SetupLoadResult result, string path) =>
        result.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == path);

    [Fact]
    public void A_plan_without_the_section_asks_for_no_volume()
    {
        var result = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known);

        Assert.Empty(result.Issues);
        Assert.Equal("", result.Plan!.Storage.Volume.Provider);
        Assert.Equal("production", result.Plan.Storage.InstanceClass);
        Assert.DoesNotContain(SetupPlanSections.Requested(result.Plan), section => section.Id == "storage");
    }

    [Fact]
    public void An_encrypting_provider_with_its_options_is_valid_and_asks_for_the_section()
    {
        var result = Load("""{ "volume": { "provider": "bitlocker-vhdx", "maxGb": 64, "serviceAccount": "NT SERVICE\\Bazaar" } }""");

        Assert.Empty(result.Issues);
        Assert.Equal(64, result.Plan!.Storage.Volume.MaxGb);
        Assert.Contains(SetupPlanSections.Requested(result.Plan), section => section.Id == "storage");
    }

    [Fact]
    public void A_linux_path_is_accepted_as_an_absolute_data_root()
    {
        var result = Load(
            """{ "volume": { "provider": "luks2", "maxGb": 64, "serviceAccount": "bazaar" } }""",
            "\"database\": { \"postgresMajor\": 18, \"dataRoot\": \"/var/lib/bazaar/data\", \"gcPartitionCount\": 16 },");

        Assert.Empty(result.Issues);
    }

    [Fact]
    public void A_volume_needs_an_absolute_data_root()
    {
        var result = Load(
            """{ "volume": { "provider": "luks2", "maxGb": 64, "serviceAccount": "bazaar" } }""",
            "\"database\": { \"postgresMajor\": 18, \"dataRoot\": null, \"gcPartitionCount\": 16 },");

        Assert.True(HasError(result, "database.dataRoot"));
    }

    [Fact]
    public void Dev_plain_is_refused_for_a_production_instance()
    {
        var result = Load("""{ "volume": { "provider": "dev-plain" } }""");

        Assert.True(HasError(result, "storage.volume.provider"));
    }

    [Fact]
    public void Dev_plain_is_valid_for_a_development_instance()
    {
        var result = Load("""{ "instanceClass": "development", "volume": { "provider": "dev-plain" } }""");

        Assert.Empty(result.Issues);
    }

    [Theory]
    [InlineData("""{ "instanceClass": "staging" }""", "storage.instanceClass")]
    [InlineData("""{ "volume": { "provider": "veracrypt" } }""", "storage.volume.provider")]
    [InlineData("""{ "volume": { "provider": "luks2", "serviceAccount": "bazaar" } }""", "storage.volume.maxGb")]
    [InlineData("""{ "volume": { "provider": "luks2", "maxGb": 64 } }""", "storage.volume.serviceAccount")]
    [InlineData("""{ "volume": { "provider": "luks2", "maxGb": 0, "serviceAccount": "bazaar" } }""", "storage.volume.maxGb")]
    [InlineData("""{ "volume": { "provider": "luks2", "containerKind": "device", "serviceAccount": "bazaar" } }""", "storage.volume.container")]
    [InlineData("""{ "volume": { "provider": "luks2", "containerKind": "tape", "maxGb": 64, "serviceAccount": "bazaar" } }""", "storage.volume.containerKind")]
    [InlineData("""{ "volume": { "provider": "luks2", "container": "volumes/c.img", "maxGb": 64, "serviceAccount": "bazaar" } }""", "storage.volume.container")]
    public void A_wrong_value_is_an_error_at_its_path(string storage, string path)
    {
        var result = Load(storage);

        Assert.True(HasError(result, path), string.Join("; ", result.Issues.Select(issue => issue.Path + ": " + issue.Message)));
    }

    [Fact]
    public void An_unknown_key_in_the_section_is_an_error()
    {
        var result = Load("""{ "volume": { "provider": "luks2", "passphrase": "x" } }""");

        Assert.NotEqual(SetupExitCode.Ok, result.ExitCode);
    }
}
