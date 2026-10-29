using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;

if (args.Length > 0 && args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
{
    return await MigrateSubcommand.RunAsync(args[1..], Console.Out);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBazaarDevAuth(builder.Configuration);
builder.Services.AddBazaarHostOptions(builder.Configuration);
builder.Services.AddBazaarHostRuntime(builder.Configuration);
builder.Services.AddBazaarSecrets();
builder.Services.AddBazaarProviders(builder.Configuration);
builder.Services.AddBazaarHostWire(builder.Configuration);
builder.Services.AddBazaarAgentSafety();
builder.Services.AddBazaarSettings();
builder.Services.AddBazaarGateway();
builder.Services.AddBazaarAgentResponder(builder.Configuration);

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
