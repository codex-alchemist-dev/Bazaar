using Bazaar.Server.Mail.Quarantine;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.MailInbound.Tests;

public sealed class MailTriageAndRegistrationTests
{
    [Fact]
    public void A_handover_is_valid_only_with_closed_codes_and_a_known_verdict()
    {
        var id = Guid.NewGuid();
        Assert.True(new MailTriageHandover(id, MailTriageVerdict.Routine, [MailTriageReasonCodes.Newsletter]).IsValid());
        Assert.True(new MailTriageHandover(id, MailTriageVerdict.NeedsAttention, []).IsValid());
        Assert.False(new MailTriageHandover(id, MailTriageVerdict.Routine, ["free text"]).IsValid());
        Assert.False(new MailTriageHandover(id, MailTriageVerdict.Routine, [MailTriageReasonCodes.InvalidHandover]).IsValid());
        Assert.False(new MailTriageHandover(id, MailTriageVerdict.Routine, [MailTriageReasonCodes.Bounce, MailTriageReasonCodes.Bounce]).IsValid());
        Assert.False(new MailTriageHandover(id, MailTriageVerdict.Routine, [.. MailTriageReasonCodes.ReaderCodes]).IsValid());
        Assert.False(new MailTriageHandover(id, (MailTriageVerdict)99, []).IsValid());
    }

    [Fact]
    public void Body_access_policy_lets_only_a_reader_or_the_owner_read()
    {
        Assert.True(MailBodyAccessPolicy.CanReadRawBody(MailAccessor.Reader));
        Assert.True(MailBodyAccessPolicy.CanReadRawBody(MailAccessor.Owner));
        Assert.False(MailBodyAccessPolicy.CanReadRawBody(MailAccessor.Agent));
        Assert.False(MailBodyAccessPolicy.CanReadRawBody(MailAccessor.System));
    }

    [Fact]
    public void Options_reject_a_non_positive_batch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InboundMailOptions { MaxBatchSize = 0 }.Validate());
    }

    [Fact]
    public void Registration_builds_the_worker_and_keeps_a_substituted_store()
    {
        var h = new Harness();
        var services = new ServiceCollection();
        var messages = new InMemoryMailMessageStore();
        services.AddSingleton<IMailMessageStore>(messages);
        services.AddSingleton<IMailQuarantineStore>(h.Quarantine);
        services.AddSingleton<IMailBodySealer>(new FakeSealer());
        services.AddSingleton(new MailScreeningOptions());
        services.AddSingleton(new MailScreeningService(new InMemoryProtectedMailDomains(), new MailScreeningOptions()));
        services.AddSingleton<IInboxGcPoster>(h.Gc);
        services.AddSingleton<IMailReaderSessions>(h.Reader);
        services.AddSingleton<IReaderSessionTaint>(h.Reader);
        services.AddSingleton<IMailTriageReader>(h.Reader);
        services.AddSingleton<IInboundMailboxDirectory>(new EmptyDirectory());
        services.AddBazaarMailInbound();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<InboundMailWorker>(provider.GetRequiredService<IInboundMailWorker>());
        Assert.IsType<InboundMailPipeline>(provider.GetRequiredService<IInboundMailPipeline>());
        Assert.Same(messages, provider.GetRequiredService<IMailMessageStore>());
    }

    [Fact]
    public void Registration_defaults_to_the_postgres_store_over_the_shared_data_source()
    {
        var services = new ServiceCollection();
        services.AddSingleton(NpgsqlDataSource.Create("Host=localhost"));
        services.AddBazaarMailInbound();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgresMailMessageStore>(provider.GetRequiredService<IMailMessageStore>());
    }

    private sealed class EmptyDirectory : IInboundMailboxDirectory
    {
        public Task<IReadOnlyList<InboundMailboxBinding>> ListActiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InboundMailboxBinding>>([]);
    }
}
