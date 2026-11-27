using Cdax.Core;
using Bazaar.Install.Abstractions;
using Bazaar.ModulePermissions;
using Bazaar.Server.ModuleHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.ModuleHost.Tests;

public sealed class ModuleHostRegistrationTests
{
    private sealed class EmptyStore : IModuleInstallStore
    {
        public Task RecordInstalledAsync(InstallAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordPermissionApprovalAsync(ModulePermissionApprovalRecord record, InstallAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(string moduleId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModulePermissionApprovalRecord>>([]);
    }

    private sealed class NoSignatures : Bazaar.Install.Trust.IModuleSignatureVerifier
    {
        public Bazaar.Install.Trust.ModuleSignatureStatus Verify(Bazaar.Install.Trust.ModulePackage package) => Bazaar.Install.Trust.ModuleSignatureStatus.UnknownKey;
    }

    private sealed class RefuseOwner : IOwnerApprovalVerifier
    {
        public bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature) => false;
    }

    private sealed class NullSink : IModulePermissionDenialSink
    {
        public void Record(ModulePermissionDenial denial)
        {
        }
    }

    [Fact]
    public async Task The_hosted_service_activates_on_start_and_without_a_package_source_nothing_is_in_force()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IModuleInstallStore, EmptyStore>();
        services.AddSingleton<Bazaar.Install.Trust.IModuleSignatureVerifier, NoSignatures>();
        services.AddSingleton<IOwnerApprovalVerifier, RefuseOwner>();
        services.AddSingleton<IModulePermissionDenialSink, NullSink>();
        services.AddBazaarModulePermissions();
        services.AddBazaarModuleHost([new ComponentManifest("gc", "1.0.0", ComponentKind.Feature, [], [])]);

        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<ModuleActivationHostedService>());

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        var state = Assert.Single(provider.GetRequiredService<ModuleActivationHost>().Snapshot());
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ModuleActivationStatus.NotPackaged, state.Status);
    }
}
