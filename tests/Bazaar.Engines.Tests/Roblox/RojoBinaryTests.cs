using Bazaar.Engines.Roblox.Rojo;

namespace Bazaar.Engines.Tests.Roblox;

/// <summary>
/// Runs a real rojo binary. Skipped unless BAZAAR_TEST_ROJO names an executable. Nothing here pins a version.
/// </summary>
public sealed class RojoBinaryTests
{
    private const string _binaryVariable = "BAZAAR_TEST_ROJO";

    private static string? Binary => Environment.GetEnvironmentVariable(_binaryVariable);

    [Fact]
    public async Task Real_rojo_reports_a_version()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(Binary), $"Set {_binaryVariable} to a rojo binary to run this test.");
        var runner = new RojoRunner(new RojoOptions { BinaryPath = Binary! });

        string version = await runner.GetVersionAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(version);
    }

    [Fact]
    public async Task Real_rojo_builds_a_minimal_project()
    {
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(Binary), $"Set {_binaryVariable} to a rojo binary to run this test.");
        string root = Directory.CreateTempSubdirectory("bazaar-rojo-real-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(root, "default.project.json"),
                "{ \"name\": \"Fixture\", \"tree\": { \"$className\": \"DataModel\" } }");
            var runner = new RojoRunner(new RojoOptions { BinaryPath = Binary! });

            string output = await runner.BuildAsync(root, "default.project.json", "fixture.rbxlx", TestContext.Current.CancellationToken);

            Assert.True(File.Exists(output));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
