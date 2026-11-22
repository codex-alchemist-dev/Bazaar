using Bazaar.Server.Host.Startup;
using Bazaar.Server.Hosting;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Host.Tests;

public sealed class HostConfigFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-kekcfg-" + Guid.NewGuid().ToString("N"));
    private const string Key = "Bazaar:Identity:KekProvider";

    public HostConfigFilesTests() => Directory.CreateDirectory(Path.Combine(_root, "config"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteKekFile(string kind) =>
        File.WriteAllText(HostConfigFiles.KekFilePath(_root), $"{{\"Bazaar\":{{\"Identity\":{{\"KekProvider\":\"{kind}\"}}}}}}");

    private static IConfigurationBuilder Base(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));

    [Fact]
    public void The_file_supplies_the_provider_when_nothing_else_sets_it()
    {
        WriteKekFile("tpm");
        var config = Base(($"{BazaarHostOptions.SectionName}:DataRoot", _root)).AddKekHostConfig().Build();

        Assert.Equal("tpm", config[Key]);
    }

    [Fact]
    public void An_environment_variable_still_wins_over_the_file()
    {
        WriteKekFile("tpm");
        Environment.SetEnvironmentVariable("BAZAARTEST__Bazaar__Identity__KekProvider", "file");
        try
        {
            var config = Base(($"{BazaarHostOptions.SectionName}:DataRoot", _root))
                .AddEnvironmentVariables("BAZAARTEST__")
                .AddKekHostConfig()
                .Build();

            Assert.Equal("file", config[Key]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAARTEST__Bazaar__Identity__KekProvider", null);
        }
    }

    [Fact]
    public void A_missing_file_changes_nothing()
    {
        var config = Base(($"{BazaarHostOptions.SectionName}:DataRoot", _root)).AddKekHostConfig().Build();

        Assert.Null(config[Key]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    public void A_blank_or_relative_data_root_adds_no_source(string? dataRoot)
    {
        var builder = Base(($"{BazaarHostOptions.SectionName}:DataRoot", dataRoot));
        var before = builder.Sources.Count;

        builder.AddKekHostConfig();

        Assert.Equal(before, builder.Sources.Count);
    }

    [Fact]
    public void Calling_twice_adds_one_source()
    {
        var builder = Base(($"{BazaarHostOptions.SectionName}:DataRoot", _root));

        builder.AddKekHostConfig().AddKekHostConfig();

        Assert.Equal(2, builder.Sources.Count);
    }
}
