using Bazaar.Server.Host;

var builder = Host.CreateApplicationBuilder(args);

// Both integrations are no-ops unless the process runs under the matching service manager.
builder.Services.AddWindowsService(options => options.ServiceName = "Bazaar.Server");
builder.Services.AddSystemd();
builder.Services.AddHostedService<StartupWorker>();

await builder.Build().RunAsync();
