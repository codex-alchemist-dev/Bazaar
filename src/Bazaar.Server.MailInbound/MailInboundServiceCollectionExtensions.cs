using Bazaar.Server.Mail.Quarantine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.MailInbound;

public static class MailInboundServiceCollectionExtensions
{
    /// <summary>
    /// Registers the inbound pipeline, the worker, the options and a Postgres <see cref="IMailMessageStore"/> over the shared <see cref="NpgsqlDataSource"/>.
    /// Every registration is <c>TryAdd</c>, so a host or test can substitute. The host supplies the ports: <see cref="IInboundMailboxDirectory"/>,
    /// <see cref="IMailBodySealer"/>, <see cref="IInboxGcPoster"/>, <see cref="IMailReaderSessions"/>, <see cref="IReaderSessionTaint"/>,
    /// <see cref="IMailTriageReader"/>, plus the Mail module's quarantine store and screening service. No background service is registered:
    /// a scheduler calls <see cref="IInboundMailWorker.RunPassAsync"/>.
    /// </summary>
    public static IServiceCollection AddBazaarMailInbound(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new InboundMailOptions());
        services.TryAddSingleton<IInboundMailEvents>(NullInboundMailEvents.Instance);
        services.TryAddSingleton<IMailMessageStore>(sp => new PostgresMailMessageStore(sp.GetRequiredService<NpgsqlDataSource>()));
        // Without a host-supplied reader, triage runs the host's model port through the mail_triage schema check.
        services.TryAddSingleton<IMailTriageReader>(sp => new SchemaCheckedMailTriageReader(sp.GetRequiredService<IMailTriageModel>()));
        services.TryAddSingleton<IInboundMailPipeline>(sp => new InboundMailPipeline(
            sp.GetRequiredService<IMailMessageStore>(),
            sp.GetRequiredService<IMailQuarantineStore>(),
            sp.GetRequiredService<IMailBodySealer>(),
            sp.GetRequiredService<MailScreeningService>(),
            sp.GetRequiredService<MailScreeningOptions>(),
            sp.GetRequiredService<IInboxGcPoster>(),
            sp.GetRequiredService<IMailReaderSessions>(),
            sp.GetRequiredService<IReaderSessionTaint>(),
            sp.GetRequiredService<IMailTriageReader>(),
            sp.GetRequiredService<InboundMailOptions>(),
            sp.GetRequiredService<IInboundMailEvents>()));
        services.TryAddSingleton<IInboundMailWorker, InboundMailWorker>();
        return services;
    }
}
