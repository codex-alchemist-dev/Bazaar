using System.Net;
using System.Text;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.WebAdmin.Http;
using Bazaar.Server.WebAdmin.Security;
using Bazaar.Server.WebAdmin.Tests.Http.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.WebAdmin.Tests.Http;

/// <summary>Runs the panel on Kestrel at 127.0.0.1 (port 0) with fake ports and a real <see cref="HttpClient"/>.</summary>
internal sealed class AdminTestHost : IAsyncDisposable
{
    public const string AllCaps = "admin.members admin.devices admin.modules kodo.view.status kodo.view.audit";

    private readonly WebApplication _app;

    private AdminTestHost(WebApplication app, HttpClient client, Uri baseUri)
    {
        _app = app;
        Client = client;
        BaseUri = baseUri;
    }

    public HttpClient Client { get; }

    public Uri BaseUri { get; }

    public string Origin => $"http://{BaseUri.Authority}";

    public IServiceProvider Services => _app.Services;

    public FakeSessionStore Sessions => Services.GetRequiredService<FakeSessionStore>();

    public FakeAudit Audit => Services.GetRequiredService<FakeAudit>();

    public FakeRedeemer Redeemer => Services.GetRequiredService<FakeRedeemer>();

    public FakeModules? Modules => Services.GetService<FakeModules>();

    public static async Task<AdminTestHost> StartAsync(bool withPorts = true, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var s = builder.Services;
        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<FakeSessionStore>();
        s.AddSingleton<IAdminSessionStore>(sp => sp.GetRequiredService<FakeSessionStore>());
        s.AddSingleton<FakeChecker>();
        s.AddSingleton<IPermissionChecker>(sp => sp.GetRequiredService<FakeChecker>());
        s.AddSingleton<IAdminSessionReader, CookieAdminSessionReader>();
        s.AddSingleton<IAdminGate, AdminGate>();
        s.AddSingleton<FakeAudit>();
        s.AddSingleton<IAdminAudit>(sp => sp.GetRequiredService<FakeAudit>());
        s.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<FakeAudit>());
        s.AddSingleton<FakeRedeemer>();
        s.AddSingleton<IAdminHandoffRedeemer>(sp => sp.GetRequiredService<FakeRedeemer>());
        s.AddSingleton<IAdminAssetProvider, FakeAssets>();
        if (withPorts)
        {
            s.AddSingleton<IAdminStatusSource, FakeStatus>();
            s.AddSingleton<IAdminKodoSource, FakeKodo>();
            s.AddSingleton<IAdminUserDirectory, FakeUsers>();
            s.AddSingleton<IAdminDeviceDirectory, FakeDevices>();
            s.AddSingleton<FakeAuditReader>();
            s.AddSingleton<IAdminAuditReader>(sp => sp.GetRequiredService<FakeAuditReader>());
            s.AddSingleton<IAdminApprovalSource, FakeApprovals>();
            s.AddSingleton<FakeModules>();
            s.AddSingleton<IAdminModuleSelection>(sp => sp.GetRequiredService<FakeModules>());
        }

        configure?.Invoke(s);
        var app = builder.Build();
        app.MapBazaarWebAdmin();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var uri = new Uri(address);
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = uri };
        return new AdminTestHost(app, client, uri);
    }

    public FakeChecker Checker => Services.GetRequiredService<FakeChecker>();

    /// <summary>The principal behind the cookie most recently seeded.</summary>
    public Guid LastPrincipal { get; private set; }

    /// <summary>Seeds a signed-in session holding <paramref name="caps"/> (all panel capabilities when none are given) and returns its cookie.</summary>
    public string Seed(params string[] caps) => SeedKind(PrincipalKind.Human, caps);

    public string SeedKind(PrincipalKind kind, params string[] caps)
    {
        var (cookie, principal) = Sessions.Seed(kind);
        Checker.Held[principal] = new HashSet<string>(caps.Length == 0 ? AllCaps.Split(' ') : caps);
        LastPrincipal = principal;
        return cookie;
    }

    public Task<HttpResponseMessage> SendAsync(
        string method,
        string path,
        string? cookie = null,
        bool csrf = false,
        bool origin = false,
        string? body = null,
        string? contentType = "application/json",
        string? originOverride = null,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (cookie is not null)
        {
            req.Headers.TryAddWithoutValidation("Cookie", "bazaar-admin=" + cookie);
        }

        if (csrf)
        {
            req.Headers.TryAddWithoutValidation("X-Bazaar-Admin", "1");
        }

        if (origin || originOverride is not null)
        {
            req.Headers.TryAddWithoutValidation("Origin", originOverride ?? Origin);
        }

        if (idempotencyKey is not null)
        {
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8);
            if (contentType is null)
            {
                req.Content.Headers.ContentType = null;
            }
            else
            {
                req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
        }

        return Client.SendAsync(req);
    }

    /// <summary>A write that passes all three write-protection checks.</summary>
    public Task<HttpResponseMessage> WriteAsync(string method, string path, string cookie, string? body = null, string? idempotencyKey = null) =>
        SendAsync(method, path, cookie, csrf: true, origin: true, body: body, idempotencyKey: idempotencyKey);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
