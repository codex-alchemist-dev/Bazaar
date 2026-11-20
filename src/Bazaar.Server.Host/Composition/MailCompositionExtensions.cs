using Bazaar.RateLimits;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Mail;
using Bazaar.Server.Mail.Quarantine;
using Bazaar.Server.MailDns;
using Bazaar.Server.MailRuntime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Wires external mail (docs/design/22-email-subsystem.md) into the host. Everything is gated on <c>Bazaar:Mail:Enabled</c>
/// (default false, so a fresh install is unchanged). Ports with no backing module stay at the mail runtime's fail-closed defaults.
/// </summary>
public static class MailCompositionExtensions
{
    /// <summary>Configuration key of the switch, <c>Bazaar:Mail:Enabled</c>.</summary>
    public const string EnabledKey = "Bazaar:Mail:Enabled";

    /// <summary>
    /// When enabled, registers the GC appender over <see cref="IGcLogWriter"/>, the Postgres quarantine store, the DNS checker and
    /// setup step, the rate limiter and the mail runtime with its two hosted loops. Needs <c>AddBazaarSecrets</c>, the audit component
    /// and the gateway's shared <see cref="NpgsqlDataSource"/>; run it after <c>AddBazaarComponents</c>.
    /// </summary>
    public static IServiceCollection AddBazaarMail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!configuration.GetValue<bool>(EnabledKey))
        {
            return services;
        }

        services.TryAddSingleton<IMailGcAppender>(sp =>
            new DelegateMailGcAppender((request, ct) => sp.GetRequiredService<IGcLogWriter>().AppendAsync(request, ct)));
        services.TryAddSingleton<IMailQuarantineStore>(sp =>
            new PostgresMailQuarantineStore(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetService<TimeProvider>()));
        services.AddBazaarRateLimits();
        services.AddBazaarMailScreening();
        services.AddBazaarMailDns();
        services.AddBazaarMailRuntime();
        return services;
    }
}
