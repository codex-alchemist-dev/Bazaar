using Bazaar.Server.Browser.Abstractions;
using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Browser;

/// <summary>
/// The browser module (doc 16 WP13, doc 14 "Browser (C22)"). It registers the launcher; the host supplies the ports
/// (profile directory, cookie source, egress policy, taint gate, guest host).
/// </summary>
public sealed class BrowserComponent : ICdaxModule
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
            WindowsProfileRoot: Pick(section, nameof(BrowserOptions.WindowsProfileRoot), d.WindowsProfileRoot));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        services.AddSingleton<IBrowserLauncher, BrowserLauncher>();
    }

    private static string Pick(IConfigurationSection section, string key, string fallback) =>
        string.IsNullOrWhiteSpace(section[key]) ? fallback : section[key]!;
}
