using Bazaar.Server.Components;
using Bazaar.Server.Components.Backup.Jobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components.Backup.Tests;

public sealed class BackupComponentTests
{
    [Fact]
    public void Manifest_is_a_feature_with_no_dependencies()
    {
        var manifest = BackupComponent.ManifestDefinition;

        Assert.Equal("backup", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
    }

    [Fact]
    public void Registration_resolves_the_run_log_in_a_validated_host()
    {
        var services = new ServiceCollection();
        new BackupComponent().AddServices(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<InMemoryBackupRunLog>(provider.GetRequiredService<IBackupRunLog>());
    }
}
