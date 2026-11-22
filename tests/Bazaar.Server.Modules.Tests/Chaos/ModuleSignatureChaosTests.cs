using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Modules.Tests.Chaos;

/// <summary>
/// Signed module chaos (doc 48 section 1): tamper with every signed field after signing, hostile signature bytes,
/// revoked and expired keys, downgrade ordering, and the start gate. Nothing may verify unless it is exactly as signed,
/// and no input may escape as an unexpected exception type.
/// </summary>
public sealed class ModuleSignatureChaosTests : IDisposable
{
    private const string _keyId = "chaos-key";
    private static readonly DateTimeOffset _now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _time = new(_now);
    private readonly InMemoryTrustedKeyStore _store = new();
    private readonly EcdsaModuleSignatureVerifier _verifier;

    public ModuleSignatureChaosTests()
    {
        _store.Add(new TrustedModuleKey(_keyId, "publisher", _key, _now.AddYears(-1), _now.AddYears(1)) { ModuleIdScopes = ["bazaar"] });
        _verifier = new EcdsaModuleSignatureVerifier(_store, _time);
    }

    public void Dispose() => _key.Dispose();

    private static HostRequirements Host() => new(1, ["linux", "windows"], ["x64"], ["kvm"]);

    private ModulePackage Signed(string id = "bazaar.sandbox", string version = "1.2.3", string[]? permissions = null)
    {
        ModulePackage unsigned = new(
            id,
            version,
            "publisher",
            new HashSet<string>(permissions ?? ["storage:local", "network:host"], StringComparer.Ordinal),
            new string('a', 64),
            _keyId,
            Host(),
            []);
        byte[] signature = _key.SignData(ModuleCanonicalForm.Build(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned with { Signature = signature };
    }

    [Fact]
    public void AnUntamperedPackageVerifies()
    {
        Assert.Equal(ModuleSignatureStatus.Valid, _verifier.Verify(Signed()));
    }

    [Fact]
    public void TamperingWithAnySignedFieldAfterSigningIsRefused()
    {
        ModulePackage good = Signed();
        ModulePackage[] tampered =
        [
            good with { ModuleId = "bazaar.sandbo" },
            good with { ModuleId = "bazaar.sandbox2" },
            good with { Version = "1.2.4" },
            good with { Version = "1.2.3-rc.1" },
            good with { ContentDigestSha256Hex = new string('b', 64) },
            good with { ContentDigestSha256Hex = new string('A', 64) },
            good with { DeclaredPermissions = new HashSet<string>(["storage:local"], StringComparer.Ordinal) },
            good with { DeclaredPermissions = new HashSet<string>(["storage:local", "network:host", "process:spawn"], StringComparer.Ordinal) },
            good with { DeclaredPermissions = new HashSet<string>(["storage:local", "network:hos"], StringComparer.Ordinal) },
            good with { Host = good.Host with { OperatingSystems = ["linux"] } },
            good with { Host = good.Host with { Architectures = ["x64", "arm64"] } },
            good with { Host = good.Host with { RequiredFeatures = [] } },
            good with { Host = good.Host with { RequiredFeatures = ["kvm", "hyperv"] } },
        ];

        foreach (ModulePackage package in tampered)
        {
            Assert.NotEqual(ModuleSignatureStatus.Valid, _verifier.Verify(package));
        }
    }

    [Fact]
    public void EverySingleBitOfTheSignatureMatters()
    {
        ModulePackage good = Signed();
        for (int bit = 0; bit < good.Signature.Length * 8; bit++)
        {
            byte[] mutated = [.. good.Signature];
            mutated[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.Equal(ModuleSignatureStatus.BadSignature, _verifier.Verify(good with { Signature = mutated }));
        }
    }

    [Fact]
    public void HostileSignatureBytesAreBadSignaturesNeverExceptions()
    {
        ModulePackage good = Signed();
        byte[][] hostile =
        [
            new byte[1],
            new byte[63],
            new byte[64],
            new byte[65],
            new byte[128],
            [.. Enumerable.Repeat((byte)0xFF, 64)],
            new byte[1_000_000],
            [.. good.Signature, 0],
            good.Signature[..63],
        ];

        foreach (byte[] signature in hostile)
        {
            Assert.Equal(ModuleSignatureStatus.BadSignature, _verifier.Verify(good with { Signature = signature }));
        }

        Assert.Equal(ModuleSignatureStatus.MissingSignature, _verifier.Verify(good with { Signature = [] }));
    }

    [Fact]
    public void NullFieldsAreMalformedNotCrashes()
    {
        ModulePackage good = Signed();

        Assert.Equal(ModuleSignatureStatus.Malformed, _verifier.Verify(good with { ModuleId = null! }));
        Assert.Equal(ModuleSignatureStatus.Malformed, _verifier.Verify(good with { DeclaredPermissions = null! }));
        Assert.Equal(ModuleSignatureStatus.Malformed, _verifier.Verify(good with { Host = null! }));
        Assert.NotEqual(ModuleSignatureStatus.Valid, _verifier.Verify(good with { Signature = null! }));
    }

    [Fact]
    public void RevokedExpiredAndNotYetValidKeysNeverVerify()
    {
        ModulePackage good = Signed();

        _time.Advance(TimeSpan.FromDays(366 * 2));
        Assert.Equal(ModuleSignatureStatus.KeyNotValid, _verifier.Verify(good));

        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        InMemoryTrustedKeyStore store = new();
        store.Add(new TrustedModuleKey(_keyId, "p", other, _now.AddYears(-1), _now.AddYears(1), Revoked: true) { ModuleIdScopes = ["bazaar"] });
        EcdsaModuleSignatureVerifier revoked = new(store, new FakeTimeProvider(_now));
        Assert.Equal(ModuleSignatureStatus.KeyNotValid, revoked.Verify(good));

        // A key that is not valid yet, and a package that names a key id nobody trusts.
        InMemoryTrustedKeyStore future = new();
        future.Add(new TrustedModuleKey(_keyId, "p", other, _now.AddDays(1), _now.AddYears(1)) { ModuleIdScopes = ["bazaar"] });
        Assert.Equal(ModuleSignatureStatus.KeyNotValid, new EcdsaModuleSignatureVerifier(future, new FakeTimeProvider(_now)).Verify(good));
        Assert.Equal(ModuleSignatureStatus.UnknownKey, _verifier.Verify(good with { SignerKeyId = "nobody" }));
    }

    [Fact]
    public void AValidSignatureFromTheWrongKeyIsRefused()
    {
        using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        ModulePackage good = Signed();
        byte[] forged = attacker.SignData(ModuleCanonicalForm.Build(good), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.Equal(ModuleSignatureStatus.BadSignature, _verifier.Verify(good with { Signature = forged }));
    }

    [Fact]
    public void ARevokedKeyStopsAnInstalledModuleAtTheStartGate()
    {
        ModulePackage good = Signed();
        ModulePermissionApprovalRecord[] approved = [Approval(good, [.. good.DeclaredPermissions])];
        AcceptAll owner = new();
        Assert.IsType<ModuleStartDecision.Startable>(ModuleStartGuard.EnsureStartable(good, _verifier, approved, owner));

        // The same package, later: the key is revoked and the content digest has been swapped on disk.
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        InMemoryTrustedKeyStore store = new();
        store.Add(new TrustedModuleKey(_keyId, "publisher", other, _now.AddYears(-1), _now.AddYears(1), Revoked: true) { ModuleIdScopes = ["bazaar"] });
        Assert.IsType<ModuleStartDecision.RefusedSignature>(ModuleStartGuard.EnsureStartable(good, new EcdsaModuleSignatureVerifier(store, _time), approved, owner));
        Assert.IsType<ModuleStartDecision.RefusedSignature>(ModuleStartGuard.EnsureStartable(good with { ContentDigestSha256Hex = new string('c', 64) }, _verifier, approved, owner));

        // A permission the owner never approved is not startable even with a perfect signature.
        Assert.IsType<ModuleStartDecision.RefusedUnapproved>(ModuleStartGuard.EnsureStartable(good, _verifier, [Approval(good, ["storage:local"])], owner));
    }

    [Fact]
    public async Task InstallRefusalsWriteNothing()
    {
        ModulePackage good = Signed();
        RecordingStore store = new();
        ModuleInstallService service = new(_verifier, new AcceptAll(), store, _time);
        HostFacts host = new("linux", "x64", new HashSet<string>(["kvm"], StringComparer.Ordinal));

        // No approval is stored yet, so even a good package on a good host is refused.
        Assert.IsType<ModuleInstallResult.RefusedSignature>(await service.InstallAsync(good with { Version = "9.9.9" }, host, "owner", TestContext.Current.CancellationToken));
        Assert.IsType<ModuleInstallResult.RefusedHost>(await service.InstallAsync(good, host with { Architecture = "arm64" }, "owner", TestContext.Current.CancellationToken));
        Assert.IsType<ModuleInstallResult.RefusedUnapproved>(await service.InstallAsync(good, host, "owner", TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Count);

        store.Approvals.Add(Approval(good, [.. good.DeclaredPermissions]));
        Assert.IsType<ModuleInstallResult.Installed>(await service.InstallAsync(good, host, "owner", TestContext.Current.CancellationToken));
        Assert.Equal(1, store.Count);
    }

    private static ModulePermissionApprovalRecord Approval(ModulePackage package, string[] permissions)
    {
        byte[] form = ModuleApprovalForm.Build(package.ModuleId, package.Version, package.PublisherId, permissions);
        return new ModulePermissionApprovalRecord(
            package.ModuleId, package.Version, permissions, Convert.ToHexStringLower(SHA256.HashData(form)), "owner-key", [1], "owner", _now);
    }

    /// <summary>Stands in for the owner's device key: these tests are about the publisher signature, the host and the stored approvals.</summary>
    private sealed class AcceptAll : IOwnerApprovalVerifier
    {
        public bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature) => true;
    }

    [Theory]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.9.9", "1.10.0")]
    [InlineData("0.99.99", "1.0.0")]
    public void VersionOrderIsSemVerSoADowngradeIsAlwaysOlder(string older, string newer)
    {
        Assert.True(ModuleVersionOrder.Compare(older, newer) < 0);
        Assert.True(ModuleVersionOrder.Compare(newer, older) > 0);
        Assert.Equal(0, ModuleVersionOrder.Compare(older, older + "+build.7"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("01.0.0")]
    [InlineData("-1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("99999999999999999999.0.0")]
    [InlineData("1.0.0\n")]
    public void MalformedVersionsAreArgumentExceptionsOnly(string version)
    {
        Assert.ThrowsAny<ArgumentException>(() => ModuleVersionOrder.Parse(version));
    }

    private sealed class RecordingStore : Bazaar.Install.Abstractions.IModuleInstallStore
    {
        private int _count;

        public int Count => _count;

        public List<ModulePermissionApprovalRecord> Approvals { get; } = [];

        public Task RecordInstalledAsync(Bazaar.Install.Abstractions.InstallAuditEntry entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }

        public Task RecordPermissionApprovalAsync(
            Bazaar.Install.Abstractions.ModulePermissionApprovalRecord record,
            Bazaar.Install.Abstractions.InstallAuditEntry entry,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<Bazaar.Install.Abstractions.ModulePermissionApprovalRecord>> ListPermissionApprovalsAsync(
            string moduleId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Bazaar.Install.Abstractions.ModulePermissionApprovalRecord>>([.. Approvals]);
    }
}
