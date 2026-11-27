using Bazaar.Core;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;
using Bazaar.Server.SealedVault;
using Bazaar.Server.Vault.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

/// <summary>The sealed-storage components in the host (doc 61, "Host settings"): loaded by default, and the sealed Vault is off until enabled.</summary>
public sealed class SealedStorageCompositionTests : IDisposable
{
    private const string _dbEnv = "BAZAAR_HOST_SEALED_TEST_DB";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-host-sealed-" + Guid.NewGuid().ToString("N"));

    public SealedStorageCompositionTests() => Environment.SetEnvironmentVariable(_dbEnv, "Host=localhost;Database=unused");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_dbEnv, null);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Dictionary<string, string?> Settings(bool vaultEnabled)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = Path.Combine(_root, "data"),
            ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + _dbEnv,
            ["Memory:Vault:RepoPath"] = Path.Combine(_root, "mount", "vault"),
            ["Memory:Vault:WriterName"] = "Writer",
            ["Memory:Vault:WriterEmail"] = "writer@example.test",
            ["Memory:Vault:AuthorEmailFormat"] = "{agent}@example.test",
        };
        if (vaultEnabled)
        {
            settings["Bazaar:SealedVault:Enabled"] = "true";
            settings["Bazaar:SealedVault:Name"] = "company";
            settings["Bazaar:SealedVault:MountRoot"] = Path.Combine(_root, "mount");
            settings["Bazaar:SealedVault:MountRootAttested"] = "true";
            settings["Bazaar:SealedGit:TempRoot"] = Path.Combine(_root, "mount", "tmp");
        }

        return settings;
    }

    private static IServiceCollection Compose(Dictionary<string, string?> settings)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddBazaarHostApplication(builder.Configuration);
        return builder.Services;
    }

    [Fact]
    public void The_default_host_lists_all_three_components_in_dependency_order()
    {
        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();

        Assert.Contains("sealed-storage", order);
        Assert.Contains("sealed-git", order);
        Assert.Contains("sealed-vault", order);
        Assert.True(order.IndexOf("identity-kek") < order.IndexOf("sealed-storage"));
        Assert.True(order.IndexOf("sealed-storage") < order.IndexOf("sealed-git"));
        Assert.True(order.IndexOf("sealed-git") < order.IndexOf("sealed-vault"));
        Assert.True(order.IndexOf("memory") < order.IndexOf("sealed-vault"));
    }

    [Fact]
    public void The_default_host_registers_the_store_the_factory_and_the_host_sink_and_leaves_the_vault_unsealed()
    {
        var services = Compose(Settings(vaultEnabled: false));

        Assert.Contains(services, d => d.ServiceType == typeof(ISealedStore));
        Assert.Contains(services, d => d.ServiceType == typeof(ISealedGitRepoFactory));
        Assert.Contains(services, d => d.ServiceType == typeof(ISealAnchor));
        Assert.Contains(services, d => d.ServiceType == typeof(ISealAuditSink));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISealedVault));

        using var sp = services.BuildServiceProvider();
        Assert.IsType<LibGit2GitProvider>(sp.GetRequiredService<IGitProvider>());
        Assert.IsType<FileSealAnchor>(sp.GetRequiredService<ISealAnchor>());
    }

    [Fact]
    public void A_host_with_no_vault_path_starts_with_the_sealed_vault_disabled()
    {
        var settings = Settings(vaultEnabled: false);
        settings.Remove("Memory:Vault:RepoPath");

        var services = Compose(settings);

        Assert.Contains(services, d => d.ServiceType == typeof(ISealedStore));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISealedVault));
    }

    [Fact]
    public void A_lease_timeout_that_is_not_a_positive_time_span_fails_the_start()
    {
        var settings = Settings(vaultEnabled: false);
        settings["Bazaar:SealedGit:LeaseTimeout"] = "soon";

        var ex = Assert.Throws<InvalidOperationException>(() => Compose(settings));

        Assert.Contains("Bazaar:SealedGit:LeaseTimeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sealed_vault_enabled_replaces_the_git_provider_with_the_decorator()
    {
        var services = Compose(Settings(vaultEnabled: true));

        using var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IGitProvider>();

        Assert.IsType<SealedGitProvider>(provider);
        Assert.Same(provider, sp.GetRequiredService<ISealedVault>());
        Assert.Equal("vault:company", sp.GetRequiredService<ISealedVault>().Scope);
        Assert.False(sp.GetRequiredService<ISealedVault>().IsMounted);
    }
}
