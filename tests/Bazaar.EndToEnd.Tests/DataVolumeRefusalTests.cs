using System.Text.Json;
using Bazaar.EndToEnd.Tests.Harness;

namespace Bazaar.EndToEnd.Tests;

/// <summary>
/// The data volume guard in the real server process (doc 63 section 5): an install with a boot root that is not a verified
/// volume ends with one line naming the reason and exit code 78, before any database is opened. No PostgreSQL is needed,
/// because every case here is refused first.
/// </summary>
public sealed class DataVolumeRefusalTests : IDisposable
{
    private const int _refused = 78;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-e2e-volume-" + Guid.NewGuid().ToString("N"));

    public DataVolumeRefusalTests()
    {
        Directory.CreateDirectory(BootRoot);
        Directory.CreateDirectory(DataRoot);
    }

    private string BootRoot => Path.Combine(_root, "boot");

    private string DataRoot => Path.Combine(_root, "data");

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

    private static readonly Dictionary<string, string> _hostEnv = new()
    {
        // Never reached: the guard refuses before a connection is opened.
        ["BAZAAR_E2E_DB"] = "Host=127.0.0.1;Port=1;Database=none;Username=none",
    };

    private string[] HostKeys(params string[] more) =>
    [
        $"--Bazaar:Host:BootRoot={BootRoot}",
        $"--Bazaar:Host:DataRoot={DataRoot}",
        "--Bazaar:Host:DatabaseConnectionReference=env:BAZAAR_E2E_DB",
        $"--Bazaar:Host:Status:Port={HostProcess.FreePort()}",
        .. more,
    ];

    private static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> env)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        return await HostProcess.RunToExitAsync(arguments, env, cts.Token);
    }

    /// <summary>Creates a development volume with the host's own <c>storage init</c> and returns its install id and volume id.</summary>
    private async Task<(string InstallId, string VolumeId)> InitDevPlainAsync()
    {
        var env = new Dictionary<string, string>
        {
            ["BAZAAR__Bazaar__Host__BootRoot"] = BootRoot,
            ["BAZAAR__Bazaar__Host__DataRoot"] = DataRoot,
            ["BAZAAR__Bazaar__Host__InstanceClass"] = "Development",
        };
        var (exitCode, output) = await RunAsync(["storage", "init", "--provider", "dev-plain"], env);
        Assert.True(exitCode == 0, output);

        using var descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(BootRoot, "volume", "descriptor.json"), TestContext.Current.CancellationToken));
        string Read(string name) => descriptor.RootElement.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        return (Read("installId"), Read("volumeId"));
    }

    [Fact]
    public async Task A_boot_root_without_a_KEK_is_refused_and_no_key_is_created()
    {
        var (exitCode, output) = await RunAsync(HostKeys(), _hostEnv);

        Assert.True(exitCode == _refused, output);
        Assert.Contains("data_volume_not_initialised", output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(BootRoot));
    }

    [Fact]
    public async Task A_volume_that_host_config_does_not_name_is_refused()
    {
        await InitDevPlainAsync();

        var (exitCode, output) = await RunAsync(HostKeys(), _hostEnv);

        Assert.True(exitCode == _refused, output);
        Assert.Contains("data_volume_not_initialised", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_development_volume_is_refused_by_a_production_host()
    {
        var (installId, volumeId) = await InitDevPlainAsync();

        var (exitCode, output) = await RunAsync(
            HostKeys($"--Bazaar:Host:InstallId={installId}", $"--Bazaar:Host:VolumeId={volumeId}", "--Bazaar:Host:InstanceClass=Development"),
            _hostEnv);

        Assert.True(exitCode == _refused, output);
        Assert.Contains("data_volume_not_encrypted", output, StringComparison.Ordinal);
    }
}
