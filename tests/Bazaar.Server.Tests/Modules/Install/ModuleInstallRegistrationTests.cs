using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Modules.Install;

public class ModuleInstallRegistrationTests
{
    private static ServiceCollection Host()
    {
        var services = new ServiceCollection();
        // The host supplies the database, the store and the trust. The gate registers none of them.
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost;Database=unused"));
        services.AddSingleton<IModuleInstallStore, InstallAuditLog>();
        services.AddSingleton<IModuleSignatureVerifier>(_ => new EcdsaModuleSignatureVerifier(new InMemoryTrustedKeyStore()));
        services.AddSingleton<IOwnerApprovalVerifier, OwnerApprovalTestKey>();
        return services;
    }

    [Fact]
    public void The_gate_does_not_register_a_signer_a_key_store_or_a_verifier()
    {
        var services = Host().AddModuleInstallGate();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IOwnerApprovalSigner));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ITrustedKeyStore));
    }

    [Fact]
    public void The_install_service_cannot_be_built_without_an_owner_approval_verifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IModuleInstallStore, InstallAuditLog>();
        services.AddSingleton<IModuleSignatureVerifier>(_ => new EcdsaModuleSignatureVerifier(new InMemoryTrustedKeyStore()));
        using var provider = services.AddModuleInstallGate().BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ModuleInstallService>());

        Assert.Contains(nameof(IOwnerApprovalVerifier), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_service_resolves_once_the_host_supplies_the_verifier_and_audit()
    {
        using var provider = Host().AddModuleInstallGate().BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ModuleInstallService>());
    }

    [Fact]
    public void The_approval_service_cannot_be_built_without_an_owner_signer()
    {
        using var provider = Host().AddModuleInstallGate().BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ModuleApprovalService>());

        Assert.Contains(nameof(IOwnerApprovalSigner), ex.Message, StringComparison.Ordinal);
    }
}
