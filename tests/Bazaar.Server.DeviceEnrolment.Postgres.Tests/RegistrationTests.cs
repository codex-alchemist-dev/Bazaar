using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.DeviceEnrolment.Postgres.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void The_extension_registers_the_postgres_store_over_the_shared_data_source()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var services = new ServiceCollection().AddSingleton(source).AddSingleton<ITransactionalAuditLog>(new RecordingAudit());

        services.AddBazaarDeviceEnrolmentPostgres();

        using var provider = services.BuildServiceProvider();
        Assert.IsType<PostgresEnrolmentStore>(provider.GetRequiredService<IEnrolmentStore>());
    }

    [Fact]
    public void A_store_the_caller_registered_first_wins()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var own = new OwnStore();
        var services = new ServiceCollection().AddSingleton(source).AddSingleton<IEnrolmentStore>(own);

        services.AddBazaarDeviceEnrolmentPostgres();

        using var provider = services.BuildServiceProvider();
        Assert.Same(own, provider.GetRequiredService<IEnrolmentStore>());
    }

    [Fact]
    public void Registering_twice_keeps_one_store()
    {
        using var source = NpgsqlDataSource.Create("Host=localhost;Database=unused");
        var services = new ServiceCollection().AddSingleton(source);

        services.AddBazaarDeviceEnrolmentPostgres().AddBazaarDeviceEnrolmentPostgres();

        Assert.Single(services, d => d.ServiceType == typeof(IEnrolmentStore));
    }

    private sealed class OwnStore : IEnrolmentStore
    {
        public Task SaveChallengeAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset expiresAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SaveCodeAsync(byte[] codeHash, Guid humanId, DateTimeOffset expiresAt, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsChallengeLiveAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> IsCodeLiveAsync(byte[] codeHash, Guid humanId, DateTimeOffset now, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeviceMatchesAsync(Guid deviceId, Guid humanId, DeviceClass deviceClass, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<EnrolmentCommitResult> CommitAsync(EnrolmentCommit commit, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
