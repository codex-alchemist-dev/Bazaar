using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;

if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
{
    return await MigrateSubcommand.RunAsync(args[1..], Console.Out);
}

if (args.Length > 0 && args[0].Equals("db", StringComparison.OrdinalIgnoreCase))
{
    return await DbSubcommand.RunAsync(args[1..], Console.Out);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBazaarHostApplication(builder.Configuration);

// Both integrations are no-ops unless the process runs under the matching service manager.
builder.Services.AddWindowsService(options => options.ServiceName = "Bazaar.Server");
builder.Services.AddSystemd();

var status = builder.Configuration.GetSection(BazaarStatusOptions.SectionName).Get<BazaarStatusOptions>() ?? new BazaarStatusOptions();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(status.Address, status.Port));

var app = builder.Build();
app.UseBazaarGetOnly();
app.MapBazaarStatus();
app.MapBazaarHttpSurface();
app.UseBazaarWire();

// Resolve before RunAsync: the container is disposed when the host stops, so resolving afterwards throws.
var exitCode = app.Services.GetRequiredService<HostExitCode>();

await app.RunAsync();
return exitCode.Value;
