using System.Security.Cryptography;
using Bazaar.Server.Install;
using Bazaar.Server.Install.Abstractions;
using Bazaar.Server.Modules.Persistence;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Tests.Modules.Install;

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

    private static ModuleApprovalService Build(IOwnerApprovalSigner signer, NpgsqlDataSource source) =>
        new(new ModuleManagerRepository(source, new NeverCalledAudit()), signer, new FakeTimeProvider(_t0));

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

        var result = await Build(signer, source).ApproveAsync("bazaar.sandbox", "0.1.0", [], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedEmpty>(result);
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task A_blank_permission_name_is_refused_and_nothing_is_signed()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var signer = new FixedSigner(new OwnerApprovalSignature("owner-device-1", [1]));

        var result = await Build(signer, source).ApproveAsync("bazaar.sandbox", "0.1.0", ["storage:local", " "], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedMalformed>(result);
        Assert.Equal(0, signer.Calls);
    }

    [Fact]
    public async Task Without_an_owner_device_key_the_approval_is_refused_and_nothing_is_recorded()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var result = await Build(new FixedSigner(null), source).ApproveAsync("bazaar.sandbox", "0.1.0", ["storage:local"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedNoOwnerSignature>(result);
    }

    [Fact]
    public async Task A_signer_that_returns_an_empty_signature_is_refused()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var result = await Build(new FixedSigner(new OwnerApprovalSignature("owner-device-1", [])), source)
            .ApproveAsync("bazaar.sandbox", "0.1.0", ["storage:local"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedNoOwnerSignature>(result);
    }

    [Fact]
    public async Task A_signer_that_returns_no_key_id_is_refused()
    {
        await using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");

        var result = await Build(new FixedSigner(new OwnerApprovalSignature(" ", [1])), source)
            .ApproveAsync("bazaar.sandbox", "0.1.0", ["storage:local"], "owner", Ct);

        Assert.IsType<ModuleApprovalResult.RefusedNoOwnerSignature>(result);
    }
}
