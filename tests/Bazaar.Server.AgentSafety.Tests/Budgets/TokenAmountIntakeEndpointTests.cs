using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Numerics;
using Bazaar.Server.AgentSafety.Http;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Http;
using Bazaar.Server.Security.Budgets;
using Bazaar.Server.Wire.Connection;
using Bazaar.Testing.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>
/// <c>POST /blobs/token-amounts</c> (doc 48, DECIDED 11.4; doc 02 HTTP surface) over a real listener and PostgreSQL: the blob
/// surface's authentication, the owner-only rule, the intake guard and the error codes.
/// </summary>
public sealed class TokenAmountIntakeEndpointTests : IAsyncDisposable
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private readonly string _blobRoot = Path.Combine(Path.GetTempPath(), "bazaar-intake-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_blobRoot))
        {
            Directory.Delete(_blobRoot, true);
        }

        return ValueTask.CompletedTask;
    }

    private async Task<(WebApplication App, HttpClient Client)> StartAsync(NpgsqlDataSource dataSource, TokenAmountBounds guard)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Http:BlobRoot"] = _blobRoot });
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new Credentials()
            .Allow("owner", new PrincipalRef(_owner, "owner"))
            .Allow("human", new PrincipalRef(Guid.NewGuid(), "human"))
            .Allow("agent", new PrincipalRef(Guid.NewGuid(), "agent")));
        builder.Services.AddSingleton(new PostgresLargeTokenAmountStore(dataSource));
        builder.Services.AddSingleton(new TokenBudgetOptions { AmountIntakeMaxDigits = guard });
        var app = builder.Build();
        app.MapBazaarTokenAmountIntake();
        await app.StartAsync(Ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static HttpRequestMessage Post(string? token, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, TokenAmountIntakeEndpoints.Route) { Content = new StringContent(body, Encoding.ASCII) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static string Huge(int digits) => "1" + new string('0', digits - 1);

    [Fact]
    public async Task The_owner_streams_an_amount_and_gets_its_sealed_reference()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, client) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var response = await client.SendAsync(Post("owner", Huge(200_001) + ".25"), Ct);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var id = Guid.Parse(json.RootElement.GetProperty("id").GetString()!);
            Assert.Equal(200_001, json.RootElement.GetProperty("integer_digits").GetInt64());
            Assert.Equal(2, json.RootElement.GetProperty("fraction_digits").GetInt64());

            await using var cmd = db.DataSource.CreateCommand(
                $"SELECT l.state || ':' || i.created_by::text FROM token_amount_large l JOIN token_amount_intake i ON i.amount_id = l.id WHERE l.id = '{id:D}'");
            Assert.Equal("sealed:" + _owner.ToString("D"), (string?)await cmd.ExecuteScalarAsync(Ct));
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData("wrong", HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData("human", HttpStatusCode.Forbidden, "forbidden")]
    [InlineData("agent", HttpStatusCode.Forbidden, "forbidden")]
    public async Task Unauthenticated_and_unauthorised_callers_are_refused_and_nothing_is_stored(string? token, HttpStatusCode status, string code)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, client) = await StartAsync(db.DataSource, TokenAmountBounds.Unlimited);
        await using (app)
        {
            var response = await client.SendAsync(Post(token, Huge(200_001)), Ct);

            Assert.Equal(status, response.StatusCode);
            Assert.Contains(code, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            Assert.Equal((0L, 0L, 0L), await LargeTierTestSupport.CountsAsync(db.DataSource, Ct));
        }
    }

    [Theory]
    [InlineData("over-length", HttpStatusCode.RequestEntityTooLarge, "over_intake_guard")]
    [InlineData("malformed", HttpStatusCode.BadRequest, "malformed_amount")]
    [InlineData("inline", HttpStatusCode.UnprocessableEntity, "fits_inline")]
    public async Task Refused_bodies_get_their_code_and_leave_nothing_sealed(string kind, HttpStatusCode status, string code)
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, client) = await StartAsync(db.DataSource, new TokenAmountBounds(250_000, 100));
        await using (app)
        {
            var body = kind switch
            {
                "over-length" => Huge(250_001),
                "malformed" => Huge(200_000) + "e5",
                _ => "12345.678",
            };

            var response = await client.SendAsync(Post("owner", body), Ct);

            Assert.Equal(status, response.StatusCode);
            Assert.Contains(code, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            Assert.Equal((0L, 0L, 0L), await LargeTierTestSupport.CountsAsync(db.DataSource, Ct));
        }
    }

    [Fact]
    public async Task A_body_over_the_guard_is_refused_while_it_is_read_when_no_length_is_declared()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var (app, client) = await StartAsync(db.DataSource, new TokenAmountBounds(200_000, 100));
        await using (app)
        {
            // Chunked transfer: no Content-Length, so only the streamed guard can stop it.
            var request = new HttpRequestMessage(HttpMethod.Post, TokenAmountIntakeEndpoints.Route)
            {
                Content = new StreamContent(new UnknownLengthStream(Encoding.ASCII.GetBytes(Huge(300_000)))),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");
            request.Headers.TransferEncodingChunked = true;

            var response = await client.SendAsync(request, Ct);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.Equal((0L, 0L, 0L), await LargeTierTestSupport.CountsAsync(db.DataSource, Ct));
        }
    }

    private sealed class Credentials : IConnectionAuthenticator
    {
        private readonly Dictionary<string, PrincipalRef> _known = new(StringComparer.Ordinal);

        public Credentials Allow(string credential, PrincipalRef principal)
        {
            _known[credential] = principal;
            return this;
        }

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential is not null && _known.TryGetValue(credential.Value, out var principal)
                ? AuthResult.Success(principal)
                : AuthResult.Fail("unknown credential"));
    }

    /// <summary>A body whose length the client cannot know in advance.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
