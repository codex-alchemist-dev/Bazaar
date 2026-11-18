using Bazaar.Contracts.Modules;
using Bazaar.Server.Install;
using Bazaar.Server.Install.Trust;
using Bazaar.Server.Tests.Modules.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Install;

public class ModuleStartGuardTests
{
    private static readonly HostRequirements _windowsSandbox = new(1, ["windows"], ["x64"], ["windows-sandbox"]);

    private static HashSet<string> Approved(params string[] permissions) => new(permissions, StringComparer.Ordinal);

    [Fact]
    public void A_verified_and_approved_module_may_start()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var package = ModuleSigningTestHelper.Sign(_windowsSandbox, key);

        var decision = ModuleStartGuard.EnsureStartable(package, verifier, Approved("storage:local"));

        Assert.IsType<ModuleStartDecision.Startable>(decision);
    }

    [Fact]
    public void A_package_altered_after_install_does_not_start()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var altered = ModuleSigningTestHelper.Sign(_windowsSandbox, key) with
        {
            DeclaredPermissions = new HashSet<string>(["storage:local", "process:spawn"], StringComparer.Ordinal),
        };

        var decision = ModuleStartGuard.EnsureStartable(altered, verifier, Approved("storage:local", "process:spawn"));

        Assert.Equal(ModuleSignatureStatus.BadSignature, Assert.IsType<ModuleStartDecision.RefusedSignature>(decision).Status);
    }

    [Fact]
    public void A_module_whose_signer_was_revoked_after_install_does_not_start()
    {
        var (_, key) = ModuleSigningTestHelper.Setup();
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey(ModuleSigningTestHelper.KeyId, "publisher-2", key, ModuleSigningTestHelper.Now.AddYears(-1), ModuleSigningTestHelper.Now.AddYears(1), Revoked: true));
        var revokedVerifier = new EcdsaModuleSignatureVerifier(store, new FakeTimeProvider(ModuleSigningTestHelper.Now));

        var decision = ModuleStartGuard.EnsureStartable(ModuleSigningTestHelper.Sign(_windowsSandbox, key), revokedVerifier, Approved("storage:local"));

        Assert.Equal(ModuleSignatureStatus.KeyNotValid, Assert.IsType<ModuleStartDecision.RefusedSignature>(decision).Status);
    }

    [Fact]
    public void A_module_with_an_unapproved_permission_does_not_start_and_the_list_is_returned()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var package = ModuleSigningTestHelper.Sign(_windowsSandbox, key, ["storage:local", "network:api.example"]);

        var decision = ModuleStartGuard.EnsureStartable(package, verifier, Approved("storage:local"));

        Assert.Equal(["network:api.example"], Assert.IsType<ModuleStartDecision.RefusedUnapproved>(decision).Unapproved);
    }

    [Fact]
    public void A_narrowed_module_starts_under_the_old_approval()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var package = ModuleSigningTestHelper.Sign(_windowsSandbox, key, ["storage:local"]);

        var decision = ModuleStartGuard.EnsureStartable(package, verifier, Approved("storage:local", "network:api.example"));

        Assert.IsType<ModuleStartDecision.Startable>(decision);
    }
}
