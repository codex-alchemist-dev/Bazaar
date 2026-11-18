using System.Globalization;
using Bazaar.Core;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin.Http;
using Bazaar.Server.WebAdmin.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.WebAdmin;

/// <summary>
/// The admin web panel as a Bazaar Component (docs/design/51-web-admin-module.md). It registers defaults only; the owning modules
/// register the section ports (<c>IAdminStatusSource</c> and the rest), and a missing port makes that section "unavailable".
/// </summary>
public sealed class WebAdminComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "web-admin",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["roles", "audit"],
        Provides: ["feature:web-admin"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ReadSessionOptions(configuration));
        services.TryAddSingleton<IAdminRevocationCheck, NoRevocation>();
        services.TryAddSingleton<IAdminSessionStore>(sp =>
            new InMemoryAdminSessionStore(sp.GetRequiredService<AdminSessionOptions>(), sp.GetRequiredService<IAdminRevocationCheck>()));

        // Fail closed: the cookie reader reads nobody until the handoff redeemer has created a session.
        services.TryAddSingleton<IAdminSessionReader, CookieAdminSessionReader>();
        services.TryAddSingleton<IAdminGate, AdminGate>();
        services.TryAddSingleton<IAdminAudit>(sp =>
            new AdminAuditRecorder(sp.GetRequiredService<IAuditLog>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IAdminAssetProvider, EmbeddedAssetProvider>();
    }

    /// <summary>Reads <c>webadmin:session:slidingMinutes</c> and <c>absoluteHours</c>; settings may tighten and never loosen past the floors.</summary>
    internal static AdminSessionOptions ReadSessionOptions(IConfiguration configuration)
    {
        var sliding = AdminSessionOptions.MaxSliding;
        var absolute = AdminSessionOptions.MaxAbsolute;
        if (double.TryParse(configuration["webadmin:session:slidingMinutes"], NumberStyles.Float, CultureInfo.InvariantCulture, out var m)
            && double.IsFinite(m) && m > 0 && m < 1e6)
        {
            sliding = TimeSpan.FromMinutes(m);
        }

        if (double.TryParse(configuration["webadmin:session:absoluteHours"], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            && double.IsFinite(h) && h > 0 && h < 1e6)
        {
            absolute = TimeSpan.FromHours(h);
        }

        return new AdminSessionOptions(sliding, absolute).Clamped();
    }
}
