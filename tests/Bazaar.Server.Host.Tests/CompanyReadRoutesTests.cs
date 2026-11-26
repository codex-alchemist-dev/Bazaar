using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Server.Calendar;
using Bazaar.Server.Host.Http;
using Bazaar.Server.Http;
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
            ValueTask.FromResult(credential?.Value == "good"
                ? AuthResult.Success(new PrincipalRef(Guid.NewGuid(), "human"))
                : AuthResult.Fail("unknown"));
    }

    private sealed class EmptyRelations : IOrgRelationReader
    {
        public Task<IReadOnlyList<OrgRelation>> ListAllAsync(bool includeEnded, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgRelation>>([]);
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

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBazaarWire(builder.Configuration);
        builder.Services.AddBazaarHttpSurface(builder.Configuration);
        builder.Services.AddBazaarCompanyReadOptions();
        builder.Services.AddSingleton<IConnectionAuthenticator, OneToken>();
        builder.Services.AddSingleton<IOrgRelationReader, EmptyRelations>();
        builder.Services.AddSingleton<ICalendarService, EmptyCalendar>();
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
