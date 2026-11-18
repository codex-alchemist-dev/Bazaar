using System.Security.Cryptography;
using Bazaar.Server.Components.Erasure;
using Bazaar.Server.Diagnostics.Adapters;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Identity.Keys;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Diagnostics.Tests.Adapters;

public sealed class AdapterTests
{
    private sealed class XorKek : IKekProvider
    {
        public string KekId => "test";

        public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken) =>
            ValueTask.FromResult(dek.ToArray().Select(b => (byte)(b ^ 0x5A)).ToArray());

        public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken) =>
            ValueTask.FromResult(wrappedDek.ToArray().Select(b => (byte)(b ^ 0x5A)).ToArray());
    }

    private sealed class FixedDirectory(Guid? owner, params Guid[] installs) : ICrashInstallDirectory
    {
        public Task<Guid?> GetOwnerAsync(Guid installId, CancellationToken ct = default) => Task.FromResult(owner);

        public Task<IReadOnlyList<Guid>> GetInstallsAsync(Guid personId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>(installs);
    }

    private sealed class RecordingErasure : ICrashErasure
    {
        public List<Guid> Seen { get; } = [];

        public Task<CrashEraseResult> EraseInstallsAsync(IReadOnlyCollection<Guid> installIds, CancellationToken ct = default)
        {
            Seen.AddRange(installIds);
            return Task.FromResult(new CrashEraseResult(2, 1));
        }
    }

    private static async Task<PgTestDatabase> NewDbAsync(CancellationToken ct) =>
        await PgTestDatabase.CreateAsync(
            "crashseal",
            async (ds, token) =>
            {
                await using var cmd = ds.CreateCommand(
                    "CREATE TABLE person_key (key_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), person_id uuid NOT NULL, wrapped_key bytea, " +
                    "created_at timestamptz NOT NULL DEFAULT now(), destroyed_at timestamptz, CHECK ((destroyed_at IS NULL) = (wrapped_key IS NOT NULL)))");
                await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            ct);

    [Fact]
    public async Task A_sealed_report_opens_for_the_same_record_and_not_for_another_kind_or_id()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewDbAsync(ct);
        var sealer = new PersonKeyCrashSealer(new PersonKeyStore(db.DataSource, new XorKek()), new FixedDirectory(Guid.NewGuid()), CrashSealingOptions.Default);
        var install = Guid.NewGuid();
        var record = Guid.NewGuid();
        var plain = "{\"report\":1}"u8.ToArray();

        var payload = await sealer.SealAsync(install, SealedPayloadKind.Report, record, plain, ct);

        Assert.Equal(plain, await sealer.OpenAsync(install, SealedPayloadKind.Report, record, payload, ct));
        Assert.DoesNotContain(plain, new[] { payload.Ciphertext });
        await Assert.ThrowsAnyAsync<CryptographicException>(() => sealer.OpenAsync(install, SealedPayloadKind.Attachment, record, payload, ct));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => sealer.OpenAsync(install, SealedPayloadKind.Report, Guid.NewGuid(), payload, ct));
    }

    [Fact]
    public async Task A_destroyed_person_key_makes_the_sealed_report_unreadable()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewDbAsync(ct);
        var person = Guid.NewGuid();
        var sealer = new PersonKeyCrashSealer(new PersonKeyStore(db.DataSource, new XorKek()), new FixedDirectory(person), CrashSealingOptions.Default);
        var record = Guid.NewGuid();
        var payload = await sealer.SealAsync(Guid.NewGuid(), SealedPayloadKind.Report, record, new byte[] { 1, 2, 3 }, ct);

        await using (var cmd = db.DataSource.CreateCommand("UPDATE person_key SET wrapped_key = NULL, destroyed_at = now() WHERE person_id = $1"))
        {
            cmd.Parameters.AddWithValue(person);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => sealer.OpenAsync(Guid.NewGuid(), SealedPayloadKind.Report, record, payload, ct));
    }

    [Fact]
    public async Task A_shared_device_is_sealed_under_the_company_key_not_a_persons()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await NewDbAsync(ct);
        var sealer = new PersonKeyCrashSealer(new PersonKeyStore(db.DataSource, new XorKek()), new FixedDirectory(null), CrashSealingOptions.Default);

        await sealer.SealAsync(Guid.NewGuid(), SealedPayloadKind.Report, Guid.NewGuid(), new byte[] { 1 }, ct);

        await using var cmd = db.DataSource.CreateCommand("SELECT person_id FROM person_key");
        Assert.Equal(CrashSealingOptions.Default.CompanyKeyOwnerId, (Guid)(await cmd.ExecuteScalarAsync(ct))!);
    }

    [Fact]
    public async Task The_erasure_participant_erases_the_persons_installs_and_reports_the_count()
    {
        var installs = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var erasure = new RecordingErasure();
        var participant = new CrashErasureParticipant(new FixedDirectory(null, installs), erasure);

        var result = await participant.EraseAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(ErasureParticipantStatus.Removed, result.Status);
        Assert.Equal(3, result.RemovedCount);
        Assert.Equal(installs, erasure.Seen);
    }

    [Fact]
    public async Task A_person_without_installs_has_nothing_to_erase()
    {
        var participant = new CrashErasureParticipant(new FixedDirectory(null), new RecordingErasure());

        var result = await participant.EraseAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(ErasureParticipantStatus.NothingToDo, result.Status);
    }

    [Fact]
    public async Task Erasure_fails_closed_when_no_install_directory_is_registered()
    {
        var participant = new CrashErasureParticipant(new UnavailableCrashInstallDirectory(), new RecordingErasure());

        var result = await participant.EraseAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(ErasureParticipantStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void The_module_registers_its_services_and_the_erasure_participant()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Database=unused"));
        services.AddSingleton<IKekProvider>(new XorKek());
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(new RecordingAudit());
        services.AddLogging();
        services.AddBazaarDiagnosticsModule(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Diagnostics:Crash:IssueRepo"] = "owner/repo",
            ["Diagnostics:Crash:IssuesPerDay"] = "5",
        }).Build());

        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IErasureParticipant>(), p => p.Name == "crash-reports");
        Assert.NotNull(provider.GetRequiredService<CrashIntakeFactory>());
        Assert.NotNull(provider.GetRequiredService<ICrashErasure>());
        var options = provider.GetRequiredService<CrashFilingOptions>();
        Assert.Equal("owner/repo", options.IssueRepo);
        Assert.Equal(5, options.IssuesPerDay);
    }
}
