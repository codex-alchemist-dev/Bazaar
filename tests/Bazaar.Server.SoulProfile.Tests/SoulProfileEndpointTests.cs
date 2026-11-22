using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SoulProfile.Tests;

public class SoulProfileEndpointTests
{
    private static readonly Guid Known = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SoulId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private sealed class Resolver : ISoulProfileCallerResolver
    {
        public Task<string?> ResolveAsync(string? header, CancellationToken cancellationToken = default) =>
            Task.FromResult(header switch { "Bearer owner" => "owner", "Bearer guest" => "guest", _ => (string?)null });
    }

    private sealed class Source : ISoulProfileSource
    {
        public Task<AgentProfileInfo?> ReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(personId == "owner" && agentId == Known
                ? new AgentProfileInfo(SoulId, "Claude", "Developer", "Haiku 5.5",
                    [new SoulVersionInfo(1, null, "aa", "{\"tone\":\"calm\"}", "bb", "owner", "first", null, DateTimeOffset.UnixEpoch)])
                : null);
    }

    private static async Task<(HttpClient Client, IAsyncDisposable Host)> StartAsync(bool ports = true, bool enabled = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        if (enabled) new SoulProfileComponent().AddServices(builder.Services, builder.Configuration);
        if (ports)
        {
            builder.Services.AddSingleton<ISoulProfileCallerResolver, Resolver>();
            builder.Services.AddSingleton<ISoulProfileSource, Source>();
        }

        var app = builder.Build();
        app.MapBazaarSoulProfile();
        await app.StartAsync();
        return (app.GetTestClient(), app);
    }

    private static HttpRequestMessage Get(string path, string? token = "owner")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task The_owner_reads_the_profile_with_snake_case_keys_and_no_charter_text()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;

        var reply = await client.SendAsync(Get($"/agents/{Known}/profile"), TestContext.Current.CancellationToken);
        var text = await reply.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("\"soul_id\":\"" + SoulId + "\"", text);
        Assert.Contains("\"model_badge\":\"Haiku 5.5\"", text);
        Assert.Contains("\"charter_sha256\":\"aa\"", text);
        Assert.DoesNotContain("charter\":", text);
    }

    [Fact]
    public async Task A_stranger_gets_401_and_every_other_miss_gets_the_same_404()
    {
        var (client, host) = await StartAsync();
        await using var _ = host;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get($"/agents/{Known}/profile", token: null), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get($"/agents/{Known}/profile", token: "nobody"), ct)).StatusCode);

        var unknown = await client.SendAsync(Get($"/agents/{Guid.NewGuid()}/profile"), ct);
        var refused = await client.SendAsync(Get($"/agents/{Known}/profile", token: "guest"), ct);
        var malformed = await client.SendAsync(Get("/agents/not-a-guid/profile"), ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(ct), await refused.Content.ReadAsStringAsync(ct));
        Assert.Equal(HttpStatusCode.NotFound, malformed.StatusCode);
    }

    [Fact]
    public async Task The_route_answers_503_without_its_ports_and_404_when_the_component_is_off()
    {
        var (noPorts, hostA) = await StartAsync(ports: false);
        await using var _a = hostA;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await noPorts.SendAsync(Get($"/agents/{Known}/profile"), TestContext.Current.CancellationToken)).StatusCode);

        var (off, hostB) = await StartAsync(enabled: false);
        await using var _b = hostB;
        Assert.Equal(HttpStatusCode.NotFound, (await off.SendAsync(Get($"/agents/{Known}/profile"), TestContext.Current.CancellationToken)).StatusCode);
    }
}

public class SoulProfileSourceTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid Soul = Guid.NewGuid();

    private sealed class Access(bool allowed) : ISoulProfileAccess
    {
        public Task<bool> CanReadAsync(string personId, Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult(allowed);
    }

    private sealed class Identities(bool known) : IAgentIdentityReader
    {
        public Task<AgentIdentity?> ReadAsync(Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(known ? new AgentIdentity("Claude", "Developer", "Haiku 5.5") : null);
    }

    private sealed class Bindings(Guid? soul) : ISoulBindingReader
    {
        public Task<Guid?> ResolveSoulAsync(Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult(soul);
    }

    private sealed class Versions : ISoulVersionReader
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<SoulVersionInfo>> ListAsync(Guid soulId, CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<SoulVersionInfo> list =
            [
                new(2, 1, "c2", "{}", "h2", "owner", "tweak", null, DateTimeOffset.UnixEpoch),
                new(1, null, "c1", "{}", "h1", "owner", "first", null, DateTimeOffset.UnixEpoch),
            ];
            return Task.FromResult(list);
        }
    }

    [Fact]
    public async Task A_readable_bound_agent_gives_identity_and_the_chain_oldest_first()
    {
        var profile = await new SoulProfileSource(new Access(true), new Identities(true), new Bindings(Soul), new Versions())
            .ReadAsync("owner", Agent, TestContext.Current.CancellationToken);

        Assert.Equal("Claude", profile!.Name);
        Assert.Equal(Soul, profile.SoulId);
        Assert.Equal([1, 2], profile.Versions.Select(v => v.Version));
    }

    [Fact]
    public async Task Every_miss_is_null_and_a_refused_reader_never_reaches_the_version_store()
    {
        var versions = new Versions();
        Assert.Null(await new SoulProfileSource(new Access(false), new Identities(true), new Bindings(Soul), versions).ReadAsync("guest", Agent, TestContext.Current.CancellationToken));
        Assert.Null(await new SoulProfileSource(new Access(true), new Identities(false), new Bindings(Soul), versions).ReadAsync("owner", Agent, TestContext.Current.CancellationToken));
        Assert.Null(await new SoulProfileSource(new Access(true), new Identities(true), new Bindings(null), versions).ReadAsync("owner", Agent, TestContext.Current.CancellationToken));
        Assert.Equal(0, versions.Calls);
    }
}

public class SoulBindingReaderTests
{
    private sealed class Bindings(Bazaar.Souls.Abstractions.SoulRef? soul) : Bazaar.Souls.Abstractions.ISoulBindings
    {
        public ValueTask<Bazaar.Souls.Abstractions.SoulRef?> ResolveAsync(Guid agentId, CancellationToken cancellationToken) => ValueTask.FromResult(soul);

        public ValueTask<bool> IsValidStampAsync(Guid agentId, Bazaar.Souls.Abstractions.SoulRef soul, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    [Fact]
    public async Task A_bound_agent_gives_its_soul_id_and_an_unbound_one_gives_null()
    {
        var soul = Guid.NewGuid();
        Assert.Equal(soul, await new SoulBindingReader(new Bindings(new Bazaar.Souls.Abstractions.SoulRef(soul, 3))).ResolveSoulAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Null(await new SoulBindingReader(new Bindings(null)).ResolveSoulAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
