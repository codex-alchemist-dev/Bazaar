using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Calendar;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Http;
using Bazaar.Server.Interrupts;
using Bazaar.Server.Org;
using Bazaar.Server.Settings;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class CompanyReadRoutesTests
{
    private sealed class OneToken : IConnectionAuthenticator
    {
        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential?.Value switch
            {
                "good" => AuthResult.Success(new PrincipalRef(Guid.NewGuid(), "human")),
                "owner" => AuthResult.Success(new PrincipalRef(Guid.NewGuid(), "owner")),
                "alice" => AuthResult.Success(new PrincipalRef(OneFrame.AgentId, "agent")),
                _ => AuthResult.Fail("unknown"),
            });
    }

    private sealed class EmptyRelations : IOrgRelationReader
    {
        public Task<IReadOnlyList<OrgRelation>> ListAllAsync(bool includeEnded, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgRelation>>([]);
    }

    private sealed class OneFrame : IInterruptStackReader
    {
        public static readonly Guid AgentId = Guid.Parse("00000000-0000-7000-8000-0000000000a1");

        public Task<IReadOnlyList<InterruptFrame>> ListLiveFramesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InterruptFrame>>(
                [new InterruptFrame(Guid.NewGuid(), AgentId, null, InterruptSource.Owner, "look at this", Guid.NewGuid(), InterruptFrameState.Active, DateTimeOffset.UnixEpoch, null, 1)]);
    }

    /// <summary>Counts pushes and pops so a refused write is seen to reach nothing.</summary>
    private sealed class CountingStack : IInterruptStackService
    {
        public int Calls;

        public Task<InterruptFrame> PushAsync(
            Guid agentId, Guid authorId, InterruptSource source, string reason, Guid gcId, DateTimeOffset? ttlAt = null, bool emergency = false,
            CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new InterruptFrame(Guid.NewGuid(), agentId, null, source, reason, gcId, InterruptFrameState.Active, DateTimeOffset.UnixEpoch, ttlAt, 1, emergency));
        }

        public Task<InterruptFrame?> PopAsync(Guid agentId, Guid frameId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<InterruptFrame?>(null);
        }

        public Task<IReadOnlyList<InterruptFrame>> GetStackAsync(Guid agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InterruptFrame>>([]);
    }

    private sealed class OneAgent : IAgentDirectoryReader
    {
        public Task<IReadOnlyList<AgentListing>> ListActiveAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentListing>>([new AgentListing(OneFrame.AgentId, "Alice")]);
    }

    private sealed class EmptyCalendar : ICalendarService
    {
        public Task<CalendarResult> CreateAsync(CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarResult> UpdateAsync(Guid id, long expectedVersion, CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarResult> ConfirmAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarResult> CancelAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarEventRecord?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<CalendarEventRecord?>(null);

        public Task<IReadOnlyList<CalendarEventRecord>> ListOverlappingAsync(DateTimeOffset from, DateTimeOffset until, Guid? agentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarEventRecord>>([]);

        public ValueTask<string?> OpenTitleAsync(CalendarEventRecord record, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }

    /// <summary>Serves the setting as "agent" so the test can see the host's setting wiring reach the response.</summary>
    private sealed class AgentTimezoneSettings : ISettingStore
    {
        public Task<T> GetAsync<T>(SettingKey<T> key, string scopeId = "", CancellationToken cancellationToken = default) =>
            Task.FromResult(ReferenceEquals(key, CalendarSettings.DisplayTimezoneKey) ? (T)(object)DisplayTimezone.Agent : key.Default);

        public Task<SettingWriteResult> SetAsync<T>(SettingKey<T> key, T value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SettingWriteResult> SetJsonAsync(string key, JsonElement value, SettingActor actor, string scopeId = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(CountingStack? stack = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        builder.Services.AddSingleton<IAgentDirectoryReader, OneAgent>();
        builder.Services.AddBazaarCompanyReadOptions();
        builder.Services.AddSingleton<IConnectionAuthenticator, OneToken>();
        builder.Services.AddSingleton<IOrgRelationReader, EmptyRelations>();
        builder.Services.AddSingleton<ICalendarService, EmptyCalendar>();
        builder.Services.AddSingleton<IInterruptStackReader, OneFrame>();
        builder.Services.AddSingleton<IInterruptStackService>(stack ?? new CountingStack());
        builder.Services.AddSingleton<ISettingStore, AgentTimezoneSettings>();
        var app = builder.Build();
        app.MapBazaarCompanyReadRoutes();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage Post(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{}") };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Theory]
    [InlineData("/company/org")]
    [InlineData("/company/calendar")]
    [InlineData("/company/interrupts")]
    public async Task Routes_answer_401_without_a_bearer_and_with_a_bad_one(string path)
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var none = await client.SendAsync(Post(path, null), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);

            var bad = await client.SendAsync(Post(path, "bad"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
    }

    [Fact]
    public async Task Org_route_answers_200_with_a_bearer()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Post("/company/org", "good"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, doc.RootElement.GetProperty("edges").GetArrayLength());
        }
    }

    [Fact]
    public async Task Interrupts_route_answers_200_with_a_bearer()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Post("/company/interrupts", "good"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var agent = Assert.Single(doc.RootElement.GetProperty("agents").EnumerateArray());
            Assert.Equal(OneFrame.AgentId.ToString(), agent.GetProperty("agentId").GetString());
            Assert.Equal("Alice", agent.GetProperty("name").GetString());
            Assert.Equal("owner", Assert.Single(agent.GetProperty("frames").EnumerateArray()).GetProperty("source").GetString());
        }
    }

    [Theory]
    [InlineData("good", "(private)")]
    [InlineData("owner", "look at this")]
    [InlineData("alice", "look at this")]
    public async Task Interrupts_route_opens_reasons_only_for_the_owner_and_the_agent_itself(string token, string expected)
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Post("/company/interrupts", token), TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            var frame = doc.RootElement.GetProperty("agents")[0].GetProperty("frames")[0];
            Assert.Equal(expected, frame.GetProperty("reason").GetString());
        }
    }

    private static HttpRequestMessage PostBody(string path, string? token, string body)
    {
        var request = Post(path, token);
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return request;
    }

    private static readonly string _pushBody =
        $$"""{"agentId":"{{OneFrame.AgentId}}","gcId":"00000000-0000-7000-8000-0000000000c3","reason":"build is red"}""";

    [Theory]
    [InlineData("/company/interrupts/push")]
    [InlineData("/company/interrupts/pop")]
    public async Task Interrupt_write_routes_answer_401_without_a_bearer(string path)
    {
        var stack = new CountingStack();
        var (app, client) = await StartAsync(stack);
        await using (app)
        {
            var response = await client.SendAsync(PostBody(path, null, _pushBody), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(0, stack.Calls);
        }
    }

    [Theory]
    [InlineData("good", "/company/interrupts/push")]
    [InlineData("alice", "/company/interrupts/push")]
    [InlineData("good", "/company/interrupts/pop")]
    [InlineData("alice", "/company/interrupts/pop")]
    public async Task Interrupt_write_routes_answer_403_to_a_non_owner(string token, string path)
    {
        var stack = new CountingStack();
        var (app, client) = await StartAsync(stack);
        await using (app)
        {
            var response = await client.SendAsync(PostBody(path, token, _pushBody), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, stack.Calls);
        }
    }

    [Fact]
    public async Task Interrupt_push_answers_200_to_the_owner()
    {
        var stack = new CountingStack();
        var (app, client) = await StartAsync(stack);
        await using (app)
        {
            var response = await client.SendAsync(PostBody("/company/interrupts/push", "owner", _pushBody), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, stack.Calls);
        }
    }

    [Fact]
    public async Task Calendar_route_answers_200_with_a_bearer_and_the_configured_timezone_mode()
    {
        var (app, client) = await StartAsync();
        await using (app)
        {
            var response = await client.SendAsync(Post("/company/calendar", "good"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("agent", doc.RootElement.GetProperty("displayTimezone").GetString());
        }
    }
}
