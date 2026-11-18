using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.WebAdmin.Tests;

public sealed class AdminGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedReader(AdminSession? session) : IAdminSessionReader
    {
        public ValueTask<AdminSession?> ReadAsync(HttpContext context, CancellationToken ct = default) => ValueTask.FromResult(session);
    }

    private sealed class FakeChecker(params string[] allowed) : IPermissionChecker
    {
        public Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default) =>
            Task.FromResult(scope == Scope.Instance && allowed.Contains(capability.Value));
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private static AdminSession Human() => new(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());

    private static AdminGate Gate(AdminSession? session, RecordingAudit audit, params string[] allowed) =>
        new(new FixedReader(session), new FakeChecker(allowed), audit, TimeProvider.System);

    [Fact]
    public async Task No_session_is_unauthenticated_and_not_audited()
    {
        var audit = new RecordingAudit();
        var access = await Gate(null, audit).CheckAsync(new DefaultHttpContext(), AdminArea.Overview, Ct);

        Assert.Equal(AdminDecision.Unauthenticated, access.Decision);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_coworker_without_admin_is_forbidden_and_audited()
    {
        var audit = new RecordingAudit();
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/admin/modules";

        var access = await Gate(Human(), audit, "read", "post").CheckAsync(ctx, AdminArea.Modules, Ct);

        Assert.Equal(AdminDecision.Forbidden, access.Decision);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("webadmin.denied", entry.Kind);
        Assert.Contains("/admin/modules", entry.Payload);
    }

    [Fact]
    public async Task An_area_needs_its_own_capability_but_the_overview_takes_any_admin_one()
    {
        var audit = new RecordingAudit();
        var gate = Gate(Human(), audit, "admin.members");

        Assert.Equal(AdminDecision.Allowed, (await gate.CheckAsync(new DefaultHttpContext(), AdminArea.Members, Ct)).Decision);
        Assert.Equal(AdminDecision.Allowed, (await gate.CheckAsync(new DefaultHttpContext(), AdminArea.Overview, Ct)).Decision);
        Assert.Equal(AdminDecision.Forbidden, (await gate.CheckAsync(new DefaultHttpContext(), AdminArea.Modules, Ct)).Decision);
    }

    [Theory]
    [InlineData(PrincipalKind.Agent)]
    [InlineData(PrincipalKind.Service)]
    public async Task A_non_human_is_always_forbidden_even_when_the_checker_would_allow(PrincipalKind kind)
    {
        var session = new AdminSession(Guid.NewGuid(), kind, Guid.NewGuid());
        var access = await Gate(session, new RecordingAudit(), "admin.modules").CheckAsync(new DefaultHttpContext(), AdminArea.Modules, Ct);

        Assert.Equal(AdminDecision.Forbidden, access.Decision);
    }

    [Fact]
    public void Every_area_maps_to_admin_capabilities_only()
    {
        foreach (var area in Enum.GetValues<AdminArea>())
            Assert.All(AdminGate.CapabilitiesFor(area), c => Assert.True(c.IsAdmin));
    }

    [Fact]
    public void The_default_wiring_reads_no_session_so_the_panel_is_closed()
    {
        var services = new ServiceCollection();
        new WebAdminComponent().AddServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(IAdminSessionReader) && d.ImplementationType == typeof(NoSessionReader));
    }

    private static async Task<int> CallAsync(AdminSession? session, params string[] allowed)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IAdminSessionReader>(new FixedReader(session));
        builder.Services.AddSingleton<IPermissionChecker>(new FakeChecker(allowed));
        builder.Services.AddSingleton<IAuditLog>(new RecordingAudit());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAdminGate, AdminGate>();
        await using var app = builder.Build();
        app.MapGet("/admin/ping", () => Results.Text("pong")).RequireAdmin(AdminArea.Overview);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).Single();
        var ctx = new DefaultHttpContext { RequestServices = app.Services };
        ctx.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(ctx);
        return ctx.Response.StatusCode;
    }

    [Fact]
    public async Task The_endpoint_filter_answers_401_403_and_200()
    {
        Assert.Equal(StatusCodes.Status401Unauthorized, await CallAsync(null));
        Assert.Equal(StatusCodes.Status403Forbidden, await CallAsync(Human(), "read"));
        Assert.Equal(StatusCodes.Status200OK, await CallAsync(Human(), "admin.roles"));
    }
}
