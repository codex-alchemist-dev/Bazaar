using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBazaarHostOptions(builder.Configuration);
builder.Services.AddBazaarHostRuntime(builder.Configuration);
builder.Services.AddBazaarSecrets();
builder.Services.AddBazaarProviders(builder.Configuration);
builder.Services.AddBazaarWire();
builder.Services.AddBazaarAgentSafety();

// Both integrations are no-ops unless the process runs under the matching service manager.
builder.Services.AddWindowsService(options => options.ServiceName = "Bazaar.Server");
builder.Services.AddSystemd();

var status = builder.Configuration.GetSection(BazaarStatusOptions.SectionName).Get<BazaarStatusOptions>() ?? new BazaarStatusOptions();
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(status.Address, status.Port));

var app = builder.Build();
app.UseBazaarGetOnly();
app.MapBazaarStatus();

await app.RunAsync();
return app.Services.GetRequiredService<HostExitCode>().Value;
