using Bazaar.Archive.Http;
using Bazaar.Community.Drafts;
using Bazaar.Community.Observatory;
using Bazaar.Core;
using Bazaar.GitHub;
using Bazaar.Server.Analytics;
using Bazaar.Server.Approvals.Http;
using Bazaar.Server.CompanyMemory;
using Bazaar.Server.Council.Http;
using Bazaar.Server.Host.Analytics;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.GeneticMemory;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Host.Status;
using Bazaar.Server.Hosting;
using Bazaar.Server.Http;
using Bazaar.Server.OfflineSync;
using Bazaar.Server.SignIn;
using Bazaar.Server.SignIn.WebAuthn.Registration;
using Bazaar.Server.Setup.Host;
using Bazaar.Server.TalentPool.Http;
using Bazaar.Server.SignIn;
using Bazaar.Server.SoulProfile;
using Bazaar.Server.Walkthrough;
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

if (args.Length > 0 && args[0].Equals("memory-graph", StringComparison.OrdinalIgnoreCase))
{
    return await MemoryGraphSubcommand.RunAsync(args[1..], Console.Out);
}

var builder = WebApplication.CreateBuilder(args);

// Doc 63 section 10: a setup plan named at start may ask for the data volume, and it must exist before PostgreSQL starts.
var volumeSetup = await VolumeSetupOnStart.RunAsync(builder.Configuration, Console.Out);
if (!volumeSetup.MayStart)
{
    return volumeSetup.ExitCode;
}

builder.Configuration.AddVolumeHostConfig();
builder.Configuration.AddKekHostConfig();

// Doc 63 section 5: a doc 63 install without its KEK is refused with one line and exit code 78, before the service graph opens it.
var bootRootRefusal = BootRootPreflight.Check(builder.Configuration, Console.Out);
if (bootRootRefusal != 0)
{
    return bootRootRefusal;
}

builder.Services.AddBazaarHostApplication(builder.Configuration);
builder.Services.AddBazaarDataVolume(builder.Configuration);

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
app.MapBazaarSignIn();
app.MapBazaarSignInWebAuthn();
app.MapBazaarHttpSurface();
app.MapBazaarOfflineSync();
app.MapBazaarGeneticMemoryHost();
app.MapCompanyMemory((http, run) => HttpSurfaceEndpoints.Guarded(http, (h, principal) => run(h, principal.Id.ToString("N"))));
app.MapArchive((http, run) => HttpSurfaceEndpoints.Guarded(http, (h, principal) => run(h, principal.Id, principal.Kind)));
app.MapCouncilEscalations((http, run) => HttpSurfaceEndpoints.Guarded(http, (h, principal) => run(h, principal.Id, principal.Kind)));
app.MapBazaarAuthWhoAmI();
app.MapBazaarGitHubRoutes(HttpSurfaceEndpoints.Guarded);
app.MapBazaarCompanyReadRoutes();
app.MapBazaarApprovalRoutes(HttpSurfaceEndpoints.Guarded);
app.MapCommunityLog();
app.MapBazaarTalentToolGrants(route => route.RequireBazaarAuth());
app.MapCommunityDraftDecision();
app.MapCommunityDraftReads();
app.MapBazaarAnalytics(AnalyticsHostAuth.ResolveAsync);
app.MapBazaarSetup();
app.MapBazaarWalkthrough();
app.MapBazaarSoulProfile();
app.MapBazaarAuthSignOut();
app.UseBazaarWire();
if (BazaarComponentRegistration.IsEnabled(app.Configuration, WireDiscoveryComponent.Id))
{
    app.MapBazaarWireDiscovery();
}

// Resolve before RunAsync: the container is disposed when the host stops, so resolving afterwards throws.
var exitCode = app.Services.GetRequiredService<HostExitCode>();

var setupStart = app.Services.GetService<SetupPlanStartOutcome>();

try
{
    await app.RunAsync();
}
catch (OperationCanceledException) when (exitCode.Value != 0)
{
    // A start step refused and stopped the host (for example the data volume guard, doc 63 section 5). The cancelled start is
    // that refusal, already logged with its reason, and not a crash: the process ends with the step's exit code.
}

// --setup-only returns the plan's exit code (doc 45 section 1.7); a serving host returns its own.
return setupStart is { ExitCode: not 0 } ? setupStart.ExitCode : exitCode.Value;
