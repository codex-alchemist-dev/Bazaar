using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.Server.Tests.Modules.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Install;

/// <summary>
/// Attacks on the module trust gate (doc 48 section 1). Each test is one way a publisher, a module or something with write
/// access to the approval table could try to get more than the owner approved, and asserts the gate refuses it.
/// </summary>
public class ModuleTrustEscalationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly HostRequirements _host = new(1, ["windows"], ["x64"], []);
    private static readonly HostFacts _windows = new("windows", "x64", new HashSet<string>(StringComparer.Ordinal));
    private static readonly DateTimeOffset _now = ModuleSigningTestHelper.Now;
    private static readonly OwnerApprovalTestKey _owner = new();

    private static ModulePackage Signed(ECDsa key, string keyId, string publisher, string moduleId, params string[] permissions)
    {
        var unsigned = new ModulePackage(
            moduleId, "1.0.0", publisher, new HashSet<string>(permissions, StringComparer.Ordinal), ModuleSigningTestHelper.Digest, keyId, _host, []);
        return ModuleSigningTestHelper.Resign(unsigned, key);
    }

    private static (EcdsaModuleSignatureVerifier Verifier, ECDsa FirstParty, ECDsa Acme) TwoPublishers()
    {
        var firstParty = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var acme = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey("bazaar-key", "bazaar", firstParty, _now.AddYears(-1), _now.AddYears(1)) { ModuleIdScopes = ["bazaar"] });
        store.Add(new TrustedModuleKey("acme-key", "acme", acme, _now.AddYears(-1), _now.AddYears(1)) { ModuleIdScopes = ["acme"] });
        return (new EcdsaModuleSignatureVerifier(store, new FakeTimeProvider(_now)), firstParty, acme);
    }

    [Fact]
    public void A_trusted_third_party_key_cannot_sign_a_first_party_module_id()
    {
        var (verifier, _, acme) = TwoPublishers();

        var hijack = Signed(acme, "acme-key", "acme", "bazaar.gc", "gc.post");

        Assert.Equal(ModuleSignatureStatus.ModuleNotInKeyScope, verifier.Verify(hijack));
    }

    [Fact]
    public void A_publisher_cannot_claim_to_be_another_publisher()
    {
        var (verifier, _, acme) = TwoPublishers();

        var impostor = Signed(acme, "acme-key", "bazaar", "acme.tools", "gc.post");

        Assert.Equal(ModuleSignatureStatus.PublisherMismatch, verifier.Verify(impostor));
    }

    [Theory]
    [InlineData("acmex")]
    [InlineData("acmex.tools")]
    [InlineData("acme-tools")]
    [InlineData("ACME.tools")]
    [InlineData("xacme")]
    [InlineData("bazaar.acme")]
    [InlineData("acme.")]
    public void A_key_scope_matches_whole_labels_only(string moduleId)
    {
        var (verifier, _, acme) = TwoPublishers();

        Assert.Equal(ModuleSignatureStatus.ModuleNotInKeyScope, verifier.Verify(Signed(acme, "acme-key", "acme", moduleId, "gc.post")));
    }

    [Fact]
    public void A_key_scope_covers_its_own_id_and_ids_below_it()
    {
        var (verifier, _, acme) = TwoPublishers();

        Assert.Equal(ModuleSignatureStatus.Valid, verifier.Verify(Signed(acme, "acme-key", "acme", "acme", "gc.post")));
        Assert.Equal(ModuleSignatureStatus.Valid, verifier.Verify(Signed(acme, "acme-key", "acme", "acme.tools.lint", "gc.post")));
    }

    [Fact]
    public void A_key_with_no_scope_signs_nothing()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey("k", "acme", key, _now.AddYears(-1), _now.AddYears(1)));
        var verifier = new EcdsaModuleSignatureVerifier(store, new FakeTimeProvider(_now));

        Assert.Equal(ModuleSignatureStatus.ModuleNotInKeyScope, verifier.Verify(Signed(key, "k", "acme", "acme.tools", "gc.post")));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("acme tools")]
    [InlineData("acmе")] // Cyrillic e
    public void A_wildcard_or_malformed_scope_covers_nothing(string scope)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trusted = new TrustedModuleKey("k", "acme", key, _now.AddYears(-1), _now.AddYears(1)) { ModuleIdScopes = [scope] };

        Assert.False(trusted.Covers("acme"));
        Assert.False(trusted.Covers("acme.tools"));
        Assert.False(trusted.Covers(scope));
    }

    [Fact]
    public void Changing_the_publisher_after_signing_breaks_the_signature()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var good = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post");

        // The key id is not signed, so the attacker points the package at their own key and publisher. The publisher id is signed.
        var repointed = good with { SignerKeyId = "acme-key", PublisherId = "acme" };

        Assert.NotEqual(ModuleSignatureStatus.Valid, verifier.Verify(repointed));
    }

    [Theory]
    [InlineData("gc.post​")] // zero-width space
    [InlineData("gc.pоst")] // Cyrillic o
    [InlineData("gc.post ")]
    [InlineData(" gc.post")]
    [InlineData("gc.post\nnetwork:*")]
    [InlineData("gc.post\0")]
    [InlineData("‮gc.post")] // right-to-left override
    public void A_permission_that_only_looks_like_another_is_malformed(string permission)
    {
        var (verifier, firstParty, _) = TwoPublishers();

        Assert.Equal(ModuleSignatureStatus.Malformed, verifier.Verify(Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", permission)));
    }

    [Theory]
    [InlineData("bazaar.gc​")]
    [InlineData("bazaar .gc")]
    [InlineData("bazaar.gc\r\n")]
    public void A_module_id_with_hidden_characters_is_malformed(string moduleId)
    {
        var (verifier, firstParty, _) = TwoPublishers();

        Assert.Equal(ModuleSignatureStatus.Malformed, verifier.Verify(Signed(firstParty, "bazaar-key", "bazaar", moduleId, "gc.post")));
    }

    [Theory]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")] // upper case: a second spelling of one digest
    [InlineData("9f86d081")]
    [InlineData("zz86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    public void A_digest_that_is_not_lowercase_sha256_hex_is_malformed(string digest)
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var package = ModuleSigningTestHelper.Resign(Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post") with { ContentDigestSha256Hex = digest }, firstParty);

        Assert.Equal(ModuleSignatureStatus.Malformed, verifier.Verify(package));
    }

    [Fact]
    public void More_permissions_than_the_limit_is_malformed()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var many = Enumerable.Range(0, ModuleText.MaxPermissions + 1).Select(i => $"p{i}").ToArray();

        Assert.Equal(ModuleSignatureStatus.Malformed, verifier.Verify(Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", many)));
    }

    [Fact]
    public async Task A_forged_row_in_the_approval_table_grants_nothing()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var package = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post", "process.spawn");
        var store = new InstallAuditLog();

        // Something with database write access inserts a well-formed row. It has the right digest but no owner key to sign with.
        var form = ModuleApprovalForm.Build(package.ModuleId, package.Version, package.PublisherId, ["gc.post", "process.spawn"]);
        store.Approvals.Add(new ModulePermissionApprovalRecord(
            package.ModuleId, package.Version, ["gc.post", "process.spawn"], Convert.ToHexStringLower(SHA256.HashData(form)),
            OwnerApprovalTestKey.KeyId, RandomNumberGenerator.GetBytes(64), "owner", _now));
        var service = new ModuleInstallService(verifier, _owner, store, new FakeTimeProvider(_now));

        var result = await service.InstallAsync(package, _windows, "owner", Ct);

        Assert.Equal(["gc.post", "process.spawn"], Assert.IsType<ModuleInstallResult.RefusedUnapproved>(result).Unapproved);
        Assert.IsType<ModuleStartDecision.RefusedUnapproved>(ModuleStartGuard.EnsureStartable(package, verifier, store.Approvals, _owner));
    }

    [Fact]
    public async Task Widening_the_permission_list_of_a_real_approval_row_voids_it()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var store = new InstallAuditLog();
        var narrow = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post");
        var real = await _owner.ApproveAsync(verifier, store, narrow, "gc.post");
        var wide = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post", "process.spawn");

        // The owner's signature and digest are kept; only the permission array is edited.
        IReadOnlyList<ModulePermissionApprovalRecord> tampered = [real with { Permissions = ["gc.post", "process.spawn"] }];

        var decision = ModuleStartGuard.EnsureStartable(wide, verifier, tampered, _owner);

        Assert.Equal(["gc.post", "process.spawn"], Assert.IsType<ModuleStartDecision.RefusedUnapproved>(decision).Unapproved);
    }

    [Fact]
    public async Task Widening_with_a_recomputed_digest_still_fails_the_owner_signature()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var store = new InstallAuditLog();
        var real = await _owner.ApproveAsync(verifier, store, Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post"), "gc.post");
        var wide = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post", "process.spawn");
        string[] widened = ["gc.post", "process.spawn"];
        var digest = Convert.ToHexStringLower(SHA256.HashData(ModuleApprovalForm.Build(real.ModuleId, real.Version, "bazaar", widened)));

        var decision = ModuleStartGuard.EnsureStartable(wide, verifier, [real with { Permissions = widened, ApprovalDigestSha256Hex = digest }], _owner);

        Assert.IsType<ModuleStartDecision.RefusedUnapproved>(decision);
    }

    [Fact]
    public async Task An_approval_for_one_module_is_not_an_approval_for_another()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var store = new InstallAuditLog();
        var real = await _owner.ApproveAsync(verifier, store, Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post"), "gc.post");
        var other = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.mail", "gc.post");

        // Offered as is, and with the module id rewritten to match.
        Assert.IsType<ModuleStartDecision.RefusedUnapproved>(ModuleStartGuard.EnsureStartable(other, verifier, [real], _owner));
        Assert.IsType<ModuleStartDecision.RefusedUnapproved>(
            ModuleStartGuard.EnsureStartable(other, verifier, [real with { ModuleId = "bazaar.mail" }], _owner));
    }

    [Fact]
    public async Task An_approval_does_not_follow_a_module_id_to_another_publisher()
    {
        // Both keys may sign under "shared". The owner approved publisher one's module; publisher two ships the same id.
        var one = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var two = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new InMemoryTrustedKeyStore();
        keys.Add(new TrustedModuleKey("k1", "one", one, _now.AddYears(-1), _now.AddYears(1)) { ModuleIdScopes = ["shared"] });
        keys.Add(new TrustedModuleKey("k2", "two", two, _now.AddYears(-1), _now.AddYears(1)) { ModuleIdScopes = ["shared"] });
        var verifier = new EcdsaModuleSignatureVerifier(keys, new FakeTimeProvider(_now));
        var store = new InstallAuditLog();
        await _owner.ApproveAsync(verifier, store, Signed(one, "k1", "one", "shared.tool", "gc.post"), "gc.post");
        var service = new ModuleInstallService(verifier, _owner, store, new FakeTimeProvider(_now));

        var result = await service.InstallAsync(Signed(two, "k2", "two", "shared.tool", "gc.post"), _windows, "owner", Ct);

        Assert.IsType<ModuleInstallResult.RefusedUnapproved>(result);
    }

    [Fact]
    public async Task The_owner_is_never_asked_to_sign_for_a_package_that_does_not_verify()
    {
        var (verifier, _, acme) = TwoPublishers();
        var store = new InstallAuditLog();
        var service = new ModuleApprovalService(verifier, store, _owner, new FakeTimeProvider(_now));

        var result = await service.ApproveAsync(Signed(acme, "acme-key", "acme", "bazaar.gc", "gc.post"), ["gc.post"], "owner", Ct);

        Assert.Equal(ModuleSignatureStatus.ModuleNotInKeyScope, Assert.IsType<ModuleApprovalResult.RefusedSignature>(result).Status);
        Assert.Empty(store.Approvals);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task A_permission_the_package_does_not_declare_cannot_be_approved_in_advance()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var store = new InstallAuditLog();
        var service = new ModuleApprovalService(verifier, store, _owner, new FakeTimeProvider(_now));

        var result = await service.ApproveAsync(
            Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post"), ["gc.post", "process.spawn"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedMalformed>(result);
        Assert.Empty(store.Approvals);
    }

    [Fact]
    public async Task A_narrowed_later_version_starts_under_the_earlier_owner_approval()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var store = new InstallAuditLog();
        await _owner.ApproveAsync(verifier, store, Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post", "storage:local"), "gc.post", "storage:local");
        var narrowed = ModuleSigningTestHelper.Resign(Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post") with { Version = "1.1.0" }, firstParty);

        Assert.IsType<ModuleStartDecision.Startable>(ModuleStartGuard.EnsureStartable(narrowed, verifier, store.Approvals, _owner));
    }

    [Fact]
    public void An_owner_verifier_that_refuses_everything_means_nothing_is_approved()
    {
        var (verifier, firstParty, _) = TwoPublishers();
        var package = Signed(firstParty, "bazaar-key", "bazaar", "bazaar.gc", "gc.post");
        var form = ModuleApprovalForm.Build(package.ModuleId, package.Version, package.PublisherId, ["gc.post"]);
        var row = new ModulePermissionApprovalRecord(
            package.ModuleId, package.Version, ["gc.post"], Convert.ToHexStringLower(SHA256.HashData(form)), "k", [1, 2, 3], "owner", _now);

        Assert.IsType<ModuleStartDecision.RefusedUnapproved>(ModuleStartGuard.EnsureStartable(package, verifier, [row], new RefuseAll()));
    }

    private sealed class RefuseAll : IOwnerApprovalVerifier
    {
        public bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature) => false;
    }
}
