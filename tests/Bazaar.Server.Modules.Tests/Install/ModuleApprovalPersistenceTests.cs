using System.Security.Cryptography;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Server.Modules.Persistence;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Audit.Db;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Modules.Tests.Install;

/// <summary>Owner approvals against the real schema (<c>modules/0002</c>) and the audit log. Needs a test PostgreSQL.</summary>
public sealed class ModuleApprovalPersistenceTests(ModuleDb db) : IClassFixture<ModuleDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _t0 = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A module id no other test in this class uses: the class shares one database.</summary>
    private static string UniqueModule() => $"mod-{Guid.NewGuid():N}";

    /// <summary>Signs each approval form with a test owner key and remembers the form, so a test can check the signature.</summary>
    private sealed class TestOwnerSigner(ECDsa key) : IOwnerApprovalSigner
    {
        public byte[]? LastForm { get; private set; }
        public byte[]? LastSignature { get; private set; }

        public Task<OwnerApprovalSignature?> SignAsync(ReadOnlyMemory<byte> approvalForm, string ownerActor, CancellationToken ct = default)
        {
            LastForm = approvalForm.ToArray();
            LastSignature = key.SignData(LastForm, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return Task.FromResult<OwnerApprovalSignature?>(new OwnerApprovalSignature("owner-device-1", LastSignature));
        }
    }

    /// <summary>A transactional audit log whose write always fails, to show that the approval row rolls back with it.</summary>
    private sealed class FailingAudit : ITransactionalAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => throw new IOException("audit sink down");

        public Task AppendInTransactionAsync(NpgsqlConnection conn, NpgsqlTransaction tx, AuditEntry e, CancellationToken ct = default) =>
            throw new IOException("audit sink down");
    }

    private ModuleApprovalService Build(IOwnerApprovalSigner signer, ITransactionalAuditLog? audit = null) =>
        new(new ModuleManagerRepository(db.DataSource, audit ?? new PostgresAuditLog(db.DataSource)), signer, new FakeTimeProvider(_t0));

    [Fact]
    public async Task An_approval_is_signed_stored_and_audited()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signer = new TestOwnerSigner(key);

        var result = await Build(signer).ApproveAsync(module, "0.1.0", ["storage:local", "network:api.example"], "owner", Ct);

        var recorded = Assert.IsType<ModuleApprovalResult.Recorded>(result).Approval;
        Assert.Equal(["network:api.example", "storage:local"], recorded.Permissions);
        Assert.True(key.VerifyData(signer.LastForm!, signer.LastSignature!, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(signer.LastForm!)), recorded.ApprovalDigestSha256Hex);

        var stored = Assert.Single(await new ModuleManagerRepository(db.DataSource, new PostgresAuditLog(db.DataSource)).ListPermissionApprovalsAsync(module, "0.1.0", Ct));
        Assert.Equal(recorded.Permissions, stored.Permissions);
        Assert.Equal("owner-device-1", stored.SignerKeyId);
        Assert.Equal(signer.LastSignature, stored.Signature);
        Assert.Equal("owner", stored.OwnerActor);

        Assert.Equal(1, await db.ScalarAsync<long>(
            "SELECT count(*) FROM audit_log WHERE kind = 'module.permissions.approved' AND payload->>'module_id' = $1", module));
    }

    [Fact]
    public async Task The_approval_and_its_audit_entry_commit_together_or_not_at_all()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        var signer = new TestOwnerSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256));

        await Assert.ThrowsAsync<IOException>(() => Build(signer, new FailingAudit()).ApproveAsync(module, "0.1.0", ["storage:local"], "owner", Ct));

        Assert.Equal(0, await db.ScalarAsync<long>("SELECT count(*) FROM module_permission_approval WHERE module_id = $1", module));
    }

    [Fact]
    public async Task The_stored_permission_set_is_sorted_and_without_repeats()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();

        await Build(new TestOwnerSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256)))
            .ApproveAsync(module, "1.0.0", ["network:b", "storage:local", "network:b"], "owner", Ct);

        var stored = Assert.Single(await new ModuleManagerRepository(db.DataSource, new PostgresAuditLog(db.DataSource)).ListPermissionApprovalsAsync(module, "1.0.0", Ct));
        Assert.Equal(["network:b", "storage:local"], stored.Permissions);
    }

    [Fact]
    public async Task A_later_approval_of_the_same_version_is_kept_and_listed_oldest_first()
    {
        ModuleTestSkip.WithoutDatabase();
        var module = UniqueModule();
        var signer = new TestOwnerSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var service = Build(signer);

        await service.ApproveAsync(module, "2.0.0", ["storage:local"], "owner", Ct);
        await service.ApproveAsync(module, "2.0.0", ["storage:local", "process:spawn"], "owner", Ct);

        var listed = await new ModuleManagerRepository(db.DataSource, new PostgresAuditLog(db.DataSource)).ListPermissionApprovalsAsync(module, "2.0.0", Ct);
        Assert.Equal(2, listed.Count);
        Assert.Equal(["storage:local"], listed[0].Permissions);
        Assert.Equal(["process:spawn", "storage:local"], listed[1].Permissions);
    }
}
