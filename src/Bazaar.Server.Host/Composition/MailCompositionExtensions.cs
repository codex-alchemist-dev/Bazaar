using Bazaar.RateLimits;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Mail;
using Bazaar.Server.Mail.Quarantine;
using Bazaar.Server.MailDns;
using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOnboarding;
using Bazaar.Server.MailOutbound;
using Bazaar.Server.MailRuntime;
using Bazaar.Server.Settings;
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
    /// setup step, mail onboarding with its GC adapter, the rate limiter and the mail runtime with its two hosted loops. Needs <c>AddBazaarSecrets</c>, the audit component
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
        // Mail bodies are per-person encrypted fields (doc 22, doc 04). Without a KEK provider the fail-closed defaults stay in place.
        services.TryAddSingleton<IMailPersonKeys>(sp =>
            new PersonKeyStoreMailKeys(new PersonKeyStore(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<IKekProvider>())));
        services.TryAddSingleton<IMailBodyProtector>(sp =>
            sp.GetService<IKekProvider>() is null ? new FailClosedMailBodyProtector() : new PersonKeyMailBodyProtector(sp.GetRequiredService<IMailPersonKeys>()));
        services.TryAddSingleton<IMailBodySealer>(sp =>
            sp.GetService<IKekProvider>() is null ? new FailClosedMailBodySealer() : new PersonKeyMailBodySealer(sp.GetRequiredService<IMailPersonKeys>()));
        // Mail onboarding (doc 22, "Mail onboarding (doc 06)"): the real inbox GC port over GC's provisioner, and the owner from the human table. The
        // provisioner is GC's own service; Ryusei registers it too, so TryAdd keeps one. No owner row means the port creates nothing.
        services.TryAddSingleton(sp => new GcCreateService(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IGcProvisioner>(sp => sp.GetRequiredService<GcCreateService>());
        services.TryAddSingleton<IMailOwnerDirectory>(sp => new PostgresMailOwnerDirectory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IInboxGcPort>(sp => new GcInboxGcPort(sp.GetRequiredService<IGcProvisioner>(), sp.GetRequiredService<IMailOwnerDirectory>()));
        // Approved recipients (doc 22, "Approved recipients source"): the operator's allow list in Postgres. Registered before the mail runtime so
        // its fail-closed default steps aside; the table starts empty, so every send stays Tier 2 until the approval path adds a row.
        services.TryAddSingleton<IApprovedRecipientStore>(sp => new PostgresApprovedRecipientStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IApprovedRecipients>(sp => new StoreApprovedRecipients(sp.GetRequiredService<IApprovedRecipientStore>()));
        // Mail settings (doc 22, "Mail settings registration"): the runtime reads its options through the settings service when the settings
        // component is present; otherwise the static options and shipped defaults stay.
        if (services.Any(d => d.ServiceType == typeof(ISettingStore)))
        {
            services.TryAddSingleton<IMailSettingReader>(sp => new SettingStoreMailSettingReader(sp.GetRequiredService<ISettingStore>()));
        }

        services.AddBazaarMailOnboarding();
        services.AddBazaarRateLimits();
        services.AddBazaarMailScreening();
        services.AddBazaarMailDns();
        services.AddBazaarMailRuntime();
        return services;
    }
}

/// <summary>Adapter of the mail module's <see cref="IMailSettingReader"/> port over the host's settings store.</summary>
internal sealed class SettingStoreMailSettingReader(ISettingStore store) : IMailSettingReader
{
    public Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct) => store.GetAsync(key, cancellationToken: ct);
}
