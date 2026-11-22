using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Blobs;
using Bazaar.Server.Http;
using Bazaar.Server.Memory.Access;
using Bazaar.Server.Tests.Vault.Data;
using Bazaar.Server.Tests.Wire.Harness;
using Bazaar.Server.Vault.Data;
using Bazaar.Server.Vault.Search;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Http;

/// <summary>
/// <c>GET /vault/search</c> and <c>GET /vault/doc</c> resolve an agent's project membership at request time from the
/// registered <see cref="IProjectMembership"/> (doc 18, "Access"), against real Postgres. Without a membership source an
/// agent reads no project area.
/// </summary>
public sealed class HttpVaultAccessTests(VaultDb db) : IClassFixture<VaultDb>, IAsyncLifetime
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-0000000000f0");
    private static readonly DateTimeOffset _t0 = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-http-vault-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _vault = Guid.NewGuid();
    private readonly Guid _xDoc = Guid.NewGuid();
    private readonly Guid _yDoc = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost)
        {
            return;
        }

        var index = new VaultIndexRepository(db.DataSource);
        await index.ReplaceDocumentAsync(Doc(_xDoc, "projects/x/Plan.md", "x"), [Block("auth plan for x")], [], Ct);
        await index.ReplaceDocumentAsync(Doc(_yDoc, "projects/y/Plan.md", "y"), [Block("auth plan for y")], [], Ct);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return ValueTask.CompletedTask;
    }

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(Bazaar.Testing.Postgres.PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private VaultDocRecord Doc(Guid docId, string path, string project) =>
        new(docId, _vault, path, path, "plan", project, "projects/" + project, null, "{}", null, _t0);

    private static VaultBlockRecord Block(string body) =>
        new(0, null, ["Plan"], 1, 2, SHA256.HashData(Encoding.UTF8.GetBytes(body)), body);

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(IProjectMembership? membership)
    {
        var values = new Dictionary<string, string?> { ["Bazaar:Http:BlobRoot"] = _root };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new FakeAuthenticator()
            .Allow("agent", new PrincipalRef(_agent, "agent"))
            .Allow("owner", new PrincipalRef(_owner, "owner")));
        builder.Services.AddSingleton<NpgsqlDataSource>(db.DataSource);
        builder.Services.AddBazaarVaultSearch();
        if (membership is not null) builder.Services.AddSingleton(membership);
        builder.Services.AddBazaarBlobs(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        var app = builder.Build();
        app.MapBazaarHttpSurface();
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static StaticProjectMembership AgentInX() => new(new Dictionary<string, ProjectMembers>
    {
        ["x"] = new(new HashSet<string> { _agent.ToString("D") }, new HashSet<string>()),
        ["y"] = new(new HashSet<string> { Guid.NewGuid().ToString("D") }, new HashSet<string>()),
    });

    private async Task<Guid[]> SearchAsync(HttpClient client, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/vault/search?vault={_vault:D}&q=auth&limit=10");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("cards").EnumerateArray().Select(card => card.GetProperty("doc_id").GetGuid()).Order().ToArray();
    }

    private async Task<HttpStatusCode> FetchAsync(HttpClient client, string token, Guid docId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/vault/doc?vault={_vault:D}&doc={docId:D}&ordinal=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task An_agent_reads_its_own_projects_documents_and_not_another_projects()
    {
        SkipWithoutDatabase();
        var (app, client) = await StartAsync(AgentInX());
        await using (app)
        {
            Assert.Equal([_xDoc], await SearchAsync(client, "agent"));
            Assert.Equal(HttpStatusCode.OK, await FetchAsync(client, "agent", _xDoc));
            Assert.Equal(HttpStatusCode.NotFound, await FetchAsync(client, "agent", _yDoc));
            Assert.Equal(new[] { _xDoc, _yDoc }.Order(), await SearchAsync(client, "owner"));
        }
    }

    [Fact]
    public async Task Without_a_membership_source_an_agent_reads_no_project_area()
    {
        SkipWithoutDatabase();
        var (app, client) = await StartAsync(membership: null);
        await using (app)
        {
            Assert.Empty(await SearchAsync(client, "agent"));
            Assert.Equal(HttpStatusCode.NotFound, await FetchAsync(client, "agent", _xDoc));
            Assert.Equal(new[] { _xDoc, _yDoc }.Order(), await SearchAsync(client, "owner"));
        }
    }
}
