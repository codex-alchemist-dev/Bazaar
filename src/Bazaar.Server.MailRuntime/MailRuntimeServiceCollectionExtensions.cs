using Bazaar.Server.MailBackends.Generic;
using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.MailRuntime;

public static class MailRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Wires the mail pipelines into a host: fail-closed defaults for ports with no backing module, real adapters where one exists, the
    /// inbound and outbound pipelines (<c>AddBazaarMailInbound</c>, <c>AddBazaarMailOutbound</c>) and the two hosted loops. Every registration is
    /// <c>TryAdd</c>, so a host registers its own port first and the default steps aside. The host must also supply an <see cref="NpgsqlDataSource"/>,
    /// <see cref="IMailGcAppender"/>, <c>ISecretStore</c>, <c>IRateLimiter</c>, <c>IAuditLog</c>, the Mail module's quarantine store and screening
    /// service, and <see cref="IMailReaderSessions"/>, <see cref="IReaderSessionTaint"/>, <see cref="IMailTriageReader"/>, <see cref="IMailBodySealer"/>,
    /// <see cref="IMailBodyProtector"/>, <see cref="IMailApprovalGateway"/> and <see cref="IInboundMailboxDirectory"/> once those exist; until then the
    /// loops do nothing (<see cref="MailRuntimeReadiness"/>).
    /// </summary>
    public static IServiceCollection AddBazaarMailRuntime(this IServiceCollection services, Action<MailRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MailRuntimeOptions();
        configure?.Invoke(options);
        services.TryAddSingleton<IMailRuntimeOptionsSource>(new StaticMailRuntimeOptionsSource(options));

        // Fail-closed defaults (doc 22, Mail runtime open items): no backing module exists for these yet.
        services.TryAddSingleton<IMailApprovalGateway, FailClosedMailApprovalGateway>();
        services.TryAddSingleton<IMailBodyProtector, FailClosedMailBodyProtector>();
        services.TryAddSingleton<IMailBodySealer, FailClosedMailBodySealer>();
        services.TryAddSingleton<IMailReaderSessions, FailClosedMailReaderSessions>();
        services.TryAddSingleton<IReaderSessionTaint, FailClosedReaderSessionTaint>();
        services.TryAddSingleton<IMailTriageReader, FailClosedMailTriageReader>();
        services.TryAddSingleton<IInboundMailboxDirectory, FailClosedInboundMailboxDirectory>();
        services.TryAddSingleton<IApprovedRecipients, FailClosedApprovedRecipients>();

        // Real adapters.
        services.TryAddSingleton<IInboxGcPoster>(sp => new InboxGcPoster(sp.GetRequiredService<IMailGcAppender>()));
        services.TryAddSingleton<IOutboundMailSettings, DefaultOutboundMailSettings>();
        services.TryAddSingleton<IBazaarAddressDirectory>(sp => new PostgresBazaarAddressDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IMailDomainSource>(sp => new PostgresMailDomainSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IMailCredentialSource>(sp => new SecretStoreMailCredentialSource(sp.GetRequiredService<Bazaar.Secrets.ISecretStore>()));
        services.TryAddSingleton<IMailBackendCatalog>(sp => new MailBackendCatalog(
            sp.GetRequiredService<IMailDomainSource>(),
            sp.GetRequiredService<Bazaar.Secrets.ISecretStore>(),
            sp.GetRequiredService<IMailCredentialSource>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IMailBackendResolver>(sp => sp.GetRequiredService<IMailBackendCatalog>());
        services.TryAddSingleton<IOutboundMailStore>(sp => new PostgresOutboundMailStore(
            sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IMailBodyProtector>()));

        services.AddBazaarMailInbound();
        services.AddBazaarMailOutbound();

        // Two loops. AddHostedService would skip a second registration of the same type, which is what a repeat call should do.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, InboundMailHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, OutboundMailResumeHostedService>());
        return services;
    }
}
