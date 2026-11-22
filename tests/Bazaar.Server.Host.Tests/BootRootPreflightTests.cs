using Bazaar.Server.DataVolume;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Host.Tests;

/// <summary>A doc 63 install without its KEK is refused before the host is built (doc 63 section 5), and nothing is created.</summary>
public sealed class BootRootPreflightTests : IDisposable
{
    private readonly string _bootRoot = Path.Combine(Path.GetTempPath(), "bazaar-preflight-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_bootRoot)) Directory.Delete(_bootRoot, recursive: true);
    }

    private IConfiguration Config(bool withBootRoot = true) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.BootRootKey] = withBootRoot ? _bootRoot : null,
            ["Bazaar:Identity:KekProvider"] = "file",
        })
        .Build();

    [Fact]
    public void An_install_without_a_boot_root_is_not_checked()
    {
        Assert.Equal(0, BootRootPreflight.Check(Config(withBootRoot: false), TextWriter.Null));
    }

    [Fact]
    public void A_missing_KEK_is_refused_with_exit_code_78_and_nothing_is_created()
    {
        var output = new StringWriter();

        var exitCode = BootRootPreflight.Check(Config(), output);

        Assert.Equal(78, exitCode);
        Assert.Contains("data_volume_not_initialised", output.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_bootRoot));
    }

    [Fact]
    public void An_existing_KEK_lets_the_start_go_on()
    {
        (KekComponent.OpenOrCreate(Config()) as IDisposable)?.Dispose();

        Assert.Equal(0, BootRootPreflight.Check(Config(), TextWriter.Null));
    }
}
