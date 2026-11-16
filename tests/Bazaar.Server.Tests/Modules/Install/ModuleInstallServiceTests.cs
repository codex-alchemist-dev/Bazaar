using Bazaar.Contracts.Modules;
using Bazaar.Install;
using Bazaar.Install.Trust;
using Bazaar.Server.Tests.Modules.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Install;

public class ModuleInstallServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HostRequirements _windowsSandbox = new(1, ["windows"], ["x64"], ["windows-sandbox"]);
    private static readonly HostFacts _windowsHost = new("windows", "x64", new HashSet<string>(["windows-sandbox"], StringComparer.Ordinal));
    private static readonly HostFacts _linuxHost = new("linux", "x64", new HashSet<string>(StringComparer.Ordinal));

    private static HashSet<string> Approved(params string[] permissions) => new(permissions, StringComparer.Ordinal);

    private static (ModuleInstallService Service, InstallAuditLog Audit) Build(IModuleSignatureVerifier verifier)
    {
        var audit = new InstallAuditLog();
        return (new ModuleInstallService(verifier, audit, new FakeTimeProvider(ModuleSigningTestHelper.Now)), audit);
    }

    [Fact]
    public async Task An_unsigned_package_is_refused_and_nothing_is_recorded()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, audit) = Build(verifier);
        var unsigned = ModuleSigningTestHelper.Sign(_windowsSandbox, key) with { Signature = [] };

        var result = await service.InstallAsync(unsigned, _windowsHost, Approved("storage:local"), "owner", Ct);

        Assert.Equal(ModuleSignatureStatus.MissingSignature, Assert.IsType<ModuleInstallResult.RefusedSignature>(result).Status);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_package_with_a_bad_signature_is_refused_and_nothing_is_recorded()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, audit) = Build(verifier);
        var tampered = ModuleSigningTestHelper.Sign(_windowsSandbox, key) with { ContentDigestSha256Hex = new string('0', 64) };

        var result = await service.InstallAsync(tampered, _windowsHost, Approved("storage:local"), "owner", Ct);

        Assert.Equal(ModuleSignatureStatus.BadSignature, Assert.IsType<ModuleInstallResult.RefusedSignature>(result).Status);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_package_on_a_host_it_cannot_run_on_is_refused_and_nothing_is_recorded()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, audit) = Build(verifier);

        var result = await service.InstallAsync(ModuleSigningTestHelper.Sign(_windowsSandbox, key), _linuxHost, Approved("storage:local"), "owner", Ct);

        var refused = Assert.IsType<ModuleInstallResult.RefusedHost>(result);
        Assert.Contains(refused.Issues, issue => issue.Code == "os-unsupported");
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_widened_permission_is_refused_and_the_refusal_names_it()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, audit) = Build(verifier);
        var package = ModuleSigningTestHelper.Sign(_windowsSandbox, key, ["storage:local", "network:api.example"]);

        var result = await service.InstallAsync(package, _windowsHost, Approved("storage:local"), "owner", Ct);

        var refused = Assert.IsType<ModuleInstallResult.RefusedUnapproved>(result);
        Assert.Equal(["network:api.example"], refused.Unapproved);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task An_approved_package_installs_and_the_install_is_audited()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, audit) = Build(verifier);

        var result = await service.InstallAsync(ModuleSigningTestHelper.Sign(_windowsSandbox, key), _windowsHost, Approved("storage:local"), "owner", Ct);

        var installed = Assert.IsType<ModuleInstallResult.Installed>(result);
        Assert.Equal("bazaar.sandbox", installed.ModuleId);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(ModuleAuditKinds.Installed, entry.Kind);
        Assert.Equal("owner", entry.Actor);
        var payload = entry.Payload;
        Assert.Equal("bazaar.sandbox", payload["module_id"]!.GetValue<string>());
        Assert.Equal("0.1.0", payload["version"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_narrowed_permission_set_installs_without_a_new_approval()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, audit) = Build(verifier);
        var package = ModuleSigningTestHelper.Sign(_windowsSandbox, key, ["storage:local"]);

        var result = await service.InstallAsync(package, _windowsHost, Approved("storage:local", "network:api.example"), "owner", Ct);

        Assert.IsType<ModuleInstallResult.Installed>(result);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task An_install_without_an_actor_is_refused_before_anything_runs()
    {
        var (verifier, key) = ModuleSigningTestHelper.Setup();
        var (service, _) = Build(verifier);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.InstallAsync(ModuleSigningTestHelper.Sign(_windowsSandbox, key), _windowsHost, Approved("storage:local"), " ", Ct));
    }
}
