using System.Xml.Linq;
using Bazaar.Admin;
using Bazaar.Server.Setup;

namespace Bazaar.Admin.Tests;

/// <summary>
/// Canary test (doc 45 section 1.6): a fake secret placed in a secret-typed key, or in a malformed file, must not appear in any
/// output of any command, in text or JSON, on either stream.
/// </summary>
public sealed class SecretCanaryTests
{
    /// <summary>The fixture with the canary placed in one input path. Each variant is one way a value could reach the output.</summary>
    private static string Variant(string name) => name switch
    {
        // Bootstrap reference: custody.kekRef. A literal is refused.
        "kekRef" => AdminFixtures.With(
            """
            "kekRef": "dpapi:C:\\ProgramData\\Bazaar\\kek.bin"
            """,
            $"\"kekRef\": \"{AdminFixtures.Canary}\""),

        // Model source key: models.sources[].keyRef. A literal is refused.
        "keyRef" => AdminFixtures.With(
            "\"secret://bazaar/providers/freellmapi/key\"",
            $"\"{AdminFixtures.Canary}\""),

        // Merge credential: custody.mergeCredential.appIdRef. A literal is refused.
        "appIdRef" => AdminFixtures.With(
            "\"secret://bazaar/owner/app-id\"",
            $"\"{AdminFixtures.Canary}\""),

        // A malformed number holding the canary: the parser error must name the line, not the text.
        "malformed" => AdminFixtures.With("\"postgresMajor\": 18", $"\"postgresMajor\": {AdminFixtures.Canary}"),

        // A wrong JSON type holding the canary: the loader names the key path only.
        "wrongType" => AdminFixtures.With("\"postgresMajor\": 18", $"\"postgresMajor\": \"{AdminFixtures.Canary}\""),

        // A settings key that looks secret, with the canary as its value.
        "settingsSecretName" => AdminFixtures.With(
            "\"host.shutdown_timeout_seconds\": 20",
            $"\"host.secret_token\": \"{AdminFixtures.Canary}\""),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown canary variant."),
    };

    [Theory]
    [InlineData("kekRef")]
    [InlineData("keyRef")]
    [InlineData("appIdRef")]
    [InlineData("malformed")]
    [InlineData("wrongType")]
    [InlineData("settingsSecretName")]
    public async Task Canary_value_appears_in_no_output_of_any_command(string variant)
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(Variant(variant));
        var zeroHash = new string('0', 64);

        var runs = new[]
        {
            await AdminRun.Async("setup", "validate", "--config", config),
            await AdminRun.Async("setup", "validate", "--config", config, "--json"),
            await AdminRun.Async("setup", "--dry-run", "--config", config),
            await AdminRun.Async("setup", "--dry-run", "--config", config, "--json"),
            await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", zeroHash),
            await AdminRun.Async("setup", "apply", "--config", config, "--plan-hash", zeroHash, "--json"),
        };

        foreach (var run in runs)
        {
            Assert.DoesNotContain(AdminFixtures.Canary, run.All);
        }
    }

    [Theory]
    [InlineData("kekRef")]
    [InlineData("keyRef")]
    [InlineData("appIdRef")]
    [InlineData("malformed")]
    [InlineData("wrongType")]
    public async Task Literal_in_a_secret_key_or_malformed_file_exits_invalid_input(string variant)
    {
        using var folder = new TempSetupFolder();
        var config = folder.Write(Variant(variant));

        var run = await AdminRun.Async("setup", "--dry-run", "--config", config);

        Assert.Equal((int)SetupExitCode.InvalidInput, run.ExitCode);
        Assert.DoesNotContain("Create ", run.Stdout);
    }
}

/// <summary>The project's own rules: the command name, and the project wiring.</summary>
public sealed class AdminProjectTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Bazaar.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Bazaar.slnx) was not found.");
    }

    [Fact]
    public void Assembly_name_in_the_project_file_matches_the_command_constant()
    {
        var project = Path.Combine(RepoRoot(), "src", "Bazaar.Admin", "Bazaar.Admin.csproj");

        var assemblyName = XDocument.Load(project).Descendants("AssemblyName").Single().Value;

        Assert.Equal(AdminConstants.CommandName, assemblyName);
    }

    [Fact]
    public void The_solution_lists_the_admin_project_and_its_tests()
    {
        var solution = File.ReadAllText(Path.Combine(RepoRoot(), "Bazaar.slnx"));

        Assert.Contains("src/Bazaar.Admin/Bazaar.Admin.csproj", solution);
        Assert.Contains("tests/Bazaar.Admin.Tests/Bazaar.Admin.Tests.csproj", solution);
    }
}
