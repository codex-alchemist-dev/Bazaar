using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Identity.Kek.Tests;

public sealed class KekComponentStartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-kekstart-" + Guid.NewGuid().ToString("N"));

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

    [Fact]
    public void Resolving_the_provider_on_a_fresh_install_creates_the_key_file_owner_only()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [KekComponent.DataRootKey] = _root })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost"));
        new KekComponent().AddServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        var kek = provider.GetRequiredService<IKekProvider>();

        var keyPath = KekProviderFactory.KeyPathFor(KekProviderFactory.DefaultKind, _root);
        Assert.NotNull(kek);
        Assert.True(File.Exists(keyPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
    }
}
