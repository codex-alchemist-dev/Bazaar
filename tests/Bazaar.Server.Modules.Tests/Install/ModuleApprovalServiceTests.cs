using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.Server.Modules.Persistence;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Modules.Tests.Install;

/// <summary>
/// The approval refusals, which never reach the database: the repository is built on a data source that is never opened, so a
/// refusal that wrongly reached it would fail the test. The recording path is in <see cref="ModuleApprovalPersistenceTests"/>.
/// </summary>
public class ModuleApprovalServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _t0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A signer that records each call and returns a fixed result.</summary>
    private sealed class FixedSigner(OwnerApprovalSignature? result) : IOwnerApprovalSigner
    {
        public int Calls { get; private set; }

        public Task<OwnerApprovalSignature?> SignAsync(ReadOnlyMemory<byte> approvalForm, string ownerActor, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static readonly ECDsa _publisherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static ModuleApprovalService Build(IOwnerApprovalSigner signer, NpgsqlDataSource source)
    {
        var keys = new InMemoryTrustedKeyStore();
        keys.Add(new TrustedModuleKey("publisher-key", "publisher", _publisherKey, _t0.AddYears(-1), _t0.AddYears(1)) { ModuleIdScopes = ["bazaar"] });
        var verifier = new EcdsaModuleSignatureVerifier(keys, new FakeTimeProvider(_t0));
        return new(verifier, new ModuleManagerRepository(source, new NeverCalledAudit()), signer, new FakeTimeProvider(_t0));
    }

    /// <summary>A publisher-signed <c>bazaar.sandbox</c> 0.1.0 that declares <c>storage:local</c>.</summary>
    private static ModulePackage Package()
    {
        var unsigned = new ModulePackage(
            "bazaar.sandbox", "0.1.0", "publisher", new HashSet<string>(StringComparer.Ordinal) { "storage:local" }, new string('a', 64), "publisher-key",
            new HostRequirements(1, ["linux"], ["x64"], []), []);
        return unsigned with
        {
            Signature = _publisherKey.SignData(ModuleCanonicalForm.Build(unsigned), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
        };
    }

    /// <summary>The refusals must never reach the audit path; any call here fails the test.</summary>
    private sealed class NeverCalledAudit : ITransactionalAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new InvalidOperationException("No approval may be audited on a refusal.");

        public Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default) =>
            throw new InvalidOperationException("No approval may be written on a refusal.");
    }

    [Fact]
    public async Task An_empty_approval_is_refused_and_nothing_is_signed()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var signer = new FixedSigner(new OwnerApprovalSignature("owner-device-1", [1]));

        var result = await Build(signer, source).ApproveAsync(Package(), [], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedEmpty>(result);
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task A_blank_permission_name_is_refused_and_nothing_is_signed()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var signer = new FixedSigner(new OwnerApprovalSignature("owner-device-1", [1]));

        var result = await Build(signer, source).ApproveAsync(Package(), ["storage:local", " "], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedMalformed>(result);
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task Without_an_owner_device_key_the_approval_is_refused_and_nothing_is_recorded()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var result = await Build(new FixedSigner(null), source).ApproveAsync(Package(), ["storage:local"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedNoOwnerSignature>(result);
    }

    [Fact]
    public async Task A_signer_that_returns_an_empty_signature_is_refused()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var result = await Build(new FixedSigner(new OwnerApprovalSignature("owner-device-1", [])), source)
            .ApproveAsync(Package(), ["storage:local"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedNoOwnerSignature>(result);
    }

    [Fact]
    public async Task A_signer_that_returns_no_key_id_is_refused()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var result = await Build(new FixedSigner(new OwnerApprovalSignature(" ", [1])), source)
            .ApproveAsync(Package(), ["storage:local"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedNoOwnerSignature>(result);
    }

    [Fact]
    public async Task A_package_whose_signature_does_not_verify_is_refused_and_nothing_is_signed()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var signer = new FixedSigner(new OwnerApprovalSignature("owner-device-1", [1]));

        var result = await Build(signer, source).ApproveAsync(Package() with { Version = "0.2.0" }, ["storage:local"], "owner", Ct);

        Assert.Equal(ModuleSignatureStatus.BadSignature, Assert.IsType<ModuleApprovalResult.RefusedSignature>(result).Status);
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task A_permission_the_package_does_not_declare_is_refused_and_nothing_is_signed()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var signer = new FixedSigner(new OwnerApprovalSignature("owner-device-1", [1]));

        var result = await Build(signer, source).ApproveAsync(Package(), ["storage:local", "process:spawn"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedMalformed>(result);
        Assert.Equal(0, signer.Calls);
    }
}
