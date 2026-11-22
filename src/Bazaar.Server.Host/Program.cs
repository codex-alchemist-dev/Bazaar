using Bazaar.Server.Host.Auth;
using Bazaar.Community.Observatory;
using Bazaar.Core;
using Bazaar.GitHub;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.OfflineSync;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.WebAdmin;
using Bazaar.Server.Wire.Connection;
using Bazaar.Server.Wire.Discovery;

if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
{
    return await MigrateSubcommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && args[0].Equals("kek", StringComparison.OrdinalIgnoreCase))
{
    return await KekSubcommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && args[0].Equals("db", StringComparison.OrdinalIgnoreCase))
{
    return await DbSubcommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && args[0].Equals("volume", StringComparison.OrdinalIgnoreCase))
{
    return await VolumeSubcommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && args[0].Equals("storage", StringComparison.OrdinalIgnoreCase))
{
    return await StorageSubcommand.RunAsync(args[1..], Console.Out);
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddKekHostConfig();

builder.Services.AddBazaarHostApplication(builder.Configuration);

// Both integrations are no-ops unless the process runs under the matching service manager.
builder.Services.AddWindowsService(options => options.ServiceName = "Bazaar.Server");
builder.Services.AddSystemd();

var status = builder.Configuration.GetSection(BazaarStatusOptions.SectionName).Get<BazaarStatusOptions>() ?? new BazaarStatusOptions();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(status.Address, status.Port));

var app = builder.Build();
app.UseBazaarGetOnly();
app.UseBazaarAuthenticatedCaller();
app.MapBazaarStatus();
app.MapBazaarWebAdmin();
app.MapBazaarHttpSurface();
app.MapBazaarOfflineSync();
app.MapBazaarAuthWhoAmI();
app.MapBazaarGitHubRoutes(HttpSurfaceEndpoints.Guarded);
app.MapBazaarCompanyReadRoutes();
app.MapCommunityLog();
app.MapBazaarSetup();
app.MapBazaarAuthSignOut();
app.UseBazaarWire();
if (BazaarComponentRegistration.IsEnabled(app.Configuration, WireDiscoveryComponent.Id))
{
    app.MapBazaarWireDiscovery();
}

// Resolve before RunAsync: the container is disposed when the host stops, so resolving afterwards throws.
var exitCode = app.Services.GetRequiredService<HostExitCode>();

var setupStart = app.Services.GetService<SetupPlanStartOutcome>();

await app.RunAsync();

// --setup-only returns the plan's exit code (doc 45 section 1.7); a serving host returns its own.
return setupStart is { ExitCode: not 0 } ? setupStart.ExitCode : exitCode.Value;
