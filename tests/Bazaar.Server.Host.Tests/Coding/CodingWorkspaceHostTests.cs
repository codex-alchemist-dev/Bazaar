using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingWorkspace;
using Bazaar.Server.CodingWorkspace.Fakes;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingWorkspaceHostTests
{
    [Fact]
    public async Task Adapter_maps_a_record_and_keeps_unknown_as_null()
    {
        var source = new InMemoryCodingRepoSource();
        source.Set(new CodingRepoRecord(new RepoRef("bazaar", "main"), "/clones/bazaar", 11, "owner", "Bazaar", 99));
        var directory = new CodingRepoDirectoryAdapter(source);

        var binding = await directory.FindAsync("bazaar", CancellationToken.None);

        Assert.Equal(new CodingRepoBinding("/clones/bazaar", 11, "owner", "Bazaar", 99), binding);
        Assert.Null(await directory.FindAsync("other", CancellationToken.None));
    }

    [Fact]
    public void Nothing_is_registered_while_the_harness_is_off()
    {
        var services = new ServiceCollection();

        services.AddBazaarCodingWorkspaceSources(new ConfigurationBuilder().Build());
        services.AddBazaarCodingWorkspaceSources(null);

        Assert.Empty(services);
    }

    [Fact]
    public async Task Enabled_registers_the_ports_and_reads_the_operator_settings()
    {
        var clone = Path.Combine(Path.GetTempPath(), "bazaar-hostws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(clone);
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "bazaar-hostws-root-" + Guid.NewGuid().ToString("N"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coding:Harness:Enabled"] = "true",
                ["Coding:Workspace:Root"] = root,
                ["Coding:Workspace:Repos:0:Id"] = "bazaar",
                ["Coding:Workspace:Repos:0:ClonePath"] = clone,
                ["Coding:Workspace:Repos:0:InstallationId"] = "11",
                ["Coding:Workspace:Repos:0:Owner"] = "owner",
                ["Coding:Workspace:Repos:0:Name"] = "Bazaar",
                ["Coding:Workspace:Repos:0:RepositoryId"] = "99",
            }).Build();
            var services = new ServiceCollection();
            services.AddBazaarCodingGit();
            services.AddSingleton<ISubagentParentWorkSource>(new InMemoryParentWorkSource());
            services.AddBazaarCodingWorkspaceSources(configuration);
            services.AddSingleton<Bazaar.Server.CodingGit.ICodingGitEngine>(new Bazaar.Server.CodingGit.Fakes.InMemoryCodingGitEngine());
            using var provider = services.BuildServiceProvider();

            Assert.Contains(services, d => d.ServiceType == typeof(ICodingRepoDirectory));
            Assert.Contains(services, d => d.ServiceType == typeof(IWorkShareLayout));
            Assert.Contains(services, d => d.ServiceType == typeof(ISubagentWorktreeAllocator));
            var binding = await provider.GetRequiredService<ICodingRepoDirectory>().FindAsync("bazaar", CancellationToken.None);
            Assert.Equal(99, binding?.RepositoryId);
            Assert.StartsWith(Path.GetFullPath(root), provider.GetRequiredService<IWorkShareLayout>().GetWorktreeRoot("charles"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(clone, recursive: true);
        }
    }

    [Fact]
    public void A_host_registration_made_first_wins()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Coding:Harness:Enabled"] = "true" }).Build();
        var services = new ServiceCollection();
        var own = new FixedLayout();
        services.AddSingleton<IWorkShareLayout>(own);

        services.AddBazaarCodingWorkspaceSources(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.Same(own, provider.GetRequiredService<IWorkShareLayout>());
    }

    private sealed class FixedLayout : IWorkShareLayout
    {
        public string GetWorktreeRoot(string agentId) => "/w";

        public string GetPassportFolder(string agentId) => "/p";
    }
}
