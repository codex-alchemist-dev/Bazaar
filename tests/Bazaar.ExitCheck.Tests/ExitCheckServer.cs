using Bazaar.Client.Core.Wire;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Gc.Wire;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// The running server as the exit check sees it: the real wire module and GC glue on in-process Kestrel, over real
/// PostgreSQL, with one GC that a human and an agent belong to.
/// </summary>
public interface IExitCheckServer : IAsyncDisposable
{
    /// <summary>The GC both principals belong to.</summary>
    Guid GcId { get; }

    /// <summary>The human member.</summary>
    Guid HumanId { get; }

    /// <summary>The agent member.</summary>
    Guid AgentId { get; }

    /// <summary>Opens WebSocket connections to the host's socket endpoint, authenticated as <paramref name="principal"/>.</summary>
    IWireTransportFactory TransportFor(Guid principal);
}

/// <summary>Starts the server under test.</summary>
public static class ExitCheckServers
{
    private const string _humanCredential = "exit-check-human";
    private const string _agentCredential = "exit-check-agent";

    /// <summary>Migrates <paramref name="db"/>, starts the host against it and seeds the GC, or throws <see cref="PendingPrerequisitesException"/>.</summary>
    public static async ValueTask<IExitCheckServer> StartAsync(PgFixture db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (ExitCheckLedger.Pending is { Count: > 0 } pending)
        {
            throw new PendingPrerequisitesException(pending);
        }

        await new MigrationRunner(db.DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);

        var human = new PrincipalRef(Guid.CreateVersion7(), "human");
        var agent = new PrincipalRef(Guid.CreateVersion7(), "agent");
        var auth = new ExitCheckAuthenticator()
            .Allow(_humanCredential, human)
            .Allow(_agentCredential, agent);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(db.DataSource);
        builder.Services.AddSingleton<IConnectionAuthenticator>(auth);
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddBazaarSync(builder.Configuration);
        builder.Services.AddBazaarGcWire();
        var app = builder.Build();
        app.UseBazaarWire();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var path = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<WireOptions>>().Value.Path;
        var socket = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + path);

        var gc = Guid.CreateVersion7();
        await ExecAsync(db.DataSource, "INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', 'exit check', $1)", gc);
        foreach (var member in new[] { human.Id, agent.Id })
        {
            await ExecAsync(db.DataSource, "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES ($1, $2, 'member', 0)", gc, member);
        }

        return new RunningServer(app, socket, gc, human.Id, agent.Id, new Dictionary<Guid, string>
        {
            [human.Id] = _humanCredential,
            [agent.Id] = _agentCredential,
        });
    }

    private static async Task ExecAsync(NpgsqlDataSource data, string sql, params object[] args)
    {
        await using var cmd = data.CreateCommand(sql);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class RunningServer(
        WebApplication app, Uri socket, Guid gcId, Guid humanId, Guid agentId, Dictionary<Guid, string> credentials) : IExitCheckServer
    {
        public Guid GcId { get; } = gcId;

        public Guid HumanId { get; } = humanId;

        public Guid AgentId { get; } = agentId;

        public IWireTransportFactory TransportFor(Guid principal) =>
            new WebSocketWireTransportFactory(socket, o => o.SetRequestHeader("Authorization", "Bearer " + credentials[principal]));

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

/// <summary>Accepts a fixed table of credentials; no identity service involved.</summary>
internal sealed class ExitCheckAuthenticator : IConnectionAuthenticator
{
    private readonly Dictionary<string, PrincipalRef> _known = new(StringComparer.Ordinal);

    public ExitCheckAuthenticator Allow(string credential, PrincipalRef principal)
    {
        _known[credential] = principal;
        return this;
    }

    public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
        ValueTask.FromResult(credential is not null && _known.TryGetValue(credential.Value, out var p) ? AuthResult.Success(p) : AuthResult.Fail("unknown credential"));
}
