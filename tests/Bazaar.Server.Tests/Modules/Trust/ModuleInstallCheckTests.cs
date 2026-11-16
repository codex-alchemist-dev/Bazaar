using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Server.Modules.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Trust;

public class ModuleInstallCheckTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private const string _keyId = "publisher-key-2";
    private const string _digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
    private static readonly HostRequirements _windowsSandbox = new(1, ["windows"], ["x64"], ["windows-sandbox"]);
    private static readonly HostFacts _windowsHost = new("windows", "x64", new HashSet<string>(["windows-sandbox"], StringComparer.Ordinal));
    private static readonly HostFacts _linuxHost = new("linux", "x64", new HashSet<string>(StringComparer.Ordinal));

    private static (EcdsaModuleSignatureVerifier Verifier, ECDsa Key) Setup() => ModuleSigningTestHelper.Setup();

    private static ModulePackage Sign(HostRequirements host, ECDsa key) => ModuleSigningTestHelper.Sign(host, key);

    [Fact]
    public void A_signed_package_runs_on_a_matching_host()
    {
        var (verifier, key) = Setup();

        var decision = ModuleInstallCheck.Evaluate(Sign(_windowsSandbox, key), verifier, _windowsHost);

        Assert.IsType<ModuleInstallDecision.Allowed>(decision);
    }

    [Fact]
    public void A_signed_package_is_refused_on_a_host_without_the_feature()
    {
        var (verifier, key) = Setup();
        var offHost = _windowsHost with { Features = new HashSet<string>(StringComparer.Ordinal) };

        var decision = ModuleInstallCheck.Evaluate(Sign(_windowsSandbox, key), verifier, offHost);

        var refused = Assert.IsType<ModuleInstallDecision.RefusedHost>(decision);
        Assert.Equal("feature-missing", Assert.Single(refused.Issues).Code);
    }

    [Fact]
    public void A_signed_package_is_refused_on_a_linux_host_and_names_the_os()
    {
        var (verifier, key) = Setup();

        var decision = ModuleInstallCheck.Evaluate(Sign(_windowsSandbox, key), verifier, _linuxHost);

        var refused = Assert.IsType<ModuleInstallDecision.RefusedHost>(decision);
        var osIssue = Assert.Single(refused.Issues, issue => issue.Code == "os-unsupported");
        Assert.Contains("linux", osIssue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_the_host_requirement_after_signing_breaks_the_signature()
    {
        var (verifier, key) = Setup();
        var signed = Sign(_windowsSandbox, key);

        var widened = signed with { Host = new HostRequirements(1, ["windows", "linux"], ["x64"], ["windows-sandbox"]) };

        var decision = ModuleInstallCheck.Evaluate(widened, verifier, _linuxHost);

        Assert.Equal(ModuleSignatureStatus.BadSignature, Assert.IsType<ModuleInstallDecision.RefusedSignature>(decision).Status);
    }

    [Fact]
    public void A_bad_signature_is_refused_before_the_host_is_checked()
    {
        var (verifier, _) = Setup();
        var unsigned = new ModulePackage("bazaar.sandbox", "0.1.0", new HashSet<string>(StringComparer.Ordinal), _digest, _keyId, _windowsSandbox, []);

        var decision = ModuleInstallCheck.Evaluate(unsigned, verifier, _windowsHost);

        Assert.Equal(ModuleSignatureStatus.MissingSignature, Assert.IsType<ModuleInstallDecision.RefusedSignature>(decision).Status);
    }

    [Fact]
    public void A_malformed_host_declaration_is_malformed_and_never_matches()
    {
        var (verifier, key) = Setup();
        var bad = new HostRequirements(1, ["plan9"], ["x64"], []);

        var status = verifier.Verify(Sign(bad, key));

        Assert.Equal(ModuleSignatureStatus.Malformed, status);
    }

    [Fact]
    public void Permissions_and_host_values_cannot_be_confused_in_the_canonical_form()
    {
        var asPermission = new ModulePackage("bazaar.sandbox", "0.1.0", new HashSet<string>(StringComparer.Ordinal) { "host.os:windows" }, _digest, _keyId, new HostRequirements(1, ["linux"], ["x64"], []), []);
        var asHost = new ModulePackage("bazaar.sandbox", "0.1.0", new HashSet<string>(StringComparer.Ordinal), _digest, _keyId, new HostRequirements(1, ["windows"], ["x64"], []), []);

        Assert.NotEqual(ModuleCanonicalForm.Build(asPermission), ModuleCanonicalForm.Build(asHost));
    }
}
