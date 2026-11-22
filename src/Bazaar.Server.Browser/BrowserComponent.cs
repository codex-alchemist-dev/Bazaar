using Bazaar.Core;
using Bazaar.Server.Browser.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Browser;

/// <summary>
/// The browser module (doc 16 WP13, doc 14 "Browser (C22)"). It registers the launcher; the host supplies the ports
/// (profile directory, cookie source, egress policy, taint gate, guest host).
/// </summary>
public sealed class BrowserComponent : IBazaarComponent
{
    public ComponentManifest Manifest { get; } = new(
        Id: "browser",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["browser:launcher"]);

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var d = new BrowserOptions();
        var section = configuration.GetSection(BrowserOptions.SectionName);
        var options = new BrowserOptions(
            LinuxExecutable: Pick(section, nameof(BrowserOptions.LinuxExecutable), d.LinuxExecutable),
            WindowsExecutable: Pick(section, nameof(BrowserOptions.WindowsExecutable), d.WindowsExecutable),
            LinuxProfileRoot: Pick(section, nameof(BrowserOptions.LinuxProfileRoot), d.LinuxProfileRoot),
            WindowsProfileRoot: Pick(section, nameof(BrowserOptions.WindowsProfileRoot), d.WindowsProfileRoot),
            MaxSessionAge: TimeSpan.TryParse(section[nameof(BrowserOptions.MaxSessionAge)], System.Globalization.CultureInfo.InvariantCulture, out var age) ? age : default);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
        services.AddSingleton<BrowserSessionSweeper>();
        services.AddSingleton(new BrowserHostOptions());

        // The guest host needs the guest pipe port; the real channel registers it when it ships. Do not require it.
        if (services.Any(d => d.ServiceType == typeof(IBrowserGuestPipe)))
        {
            services.TryAddSingleton<IBrowserGuestHost, GuestPipeBrowserHost>();
        }
    }

    private static string Pick(IConfigurationSection section, string key, string fallback) =>
        string.IsNullOrWhiteSpace(section[key]) ? fallback : section[key]!;
}
