using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.MailOnboarding;

public static class MailOnboardingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mail side of agent onboarding. Every registration is <c>TryAdd</c>, so a host registers its own port first and the default steps
    /// aside. The host must supply an <see cref="NpgsqlDataSource"/>; an <see cref="IAuditLog"/> is used when present. <see cref="IInboxGcPort"/> fails
    /// closed until the GC provisioner adapter exists, so onboarding reports <see cref="MailOnboardingStatus.InboxGcUnavailable"/> and binds nothing.
    /// </summary>
    public static IServiceCollection AddBazaarMailOnboarding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IInboxGcPort, FailClosedInboxGcPort>();
        services.TryAddSingleton<IMailboxPersonStore>(sp => new PostgresMailboxPersonStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IMailOnboarding>(sp => new MailOnboardingService(
            sp.GetRequiredService<IMailboxPersonStore>(),
            sp.GetRequiredService<IInboxGcPort>(),
            sp.GetService<IAuditLog>(),
            sp.GetService<TimeProvider>()));
        return services;
    }
}
