using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Org.Tests;

/// <summary>
/// <c>POST /company/org</c> on a real loopback Kestrel. The client's reader lives in a client module this server module may not
/// reference, so the shape is asserted field by field against what <c>OrgSnapshotReader</c> parses (doc 24 section 5).
/// </summary>
public sealed class OrgReadRouteTests : IAsyncLifetime
{
    private static readonly Guid _alice = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-0000000000b2");
    private static readonly Guid _project = Guid.Parse("00000000-0000-7000-8000-0000000000c3");

    private readonly FakeRelations _relations = new();
    private IResult? _refusal;
    private int _guardCalls;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOrgRelationReader>(_relations);
        builder.Services.AddSingleton<IOrgDirectory>(new FakeDirectory());

        _app = builder.Build();
        _app.MapCompanyOrg(_ =>
        {
            _guardCalls++;
            return ValueTask.FromResult(_refusal);
        });
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Post(string body) =>
        await _http.PostAsync("/company/org", new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task Answers_the_pane_model_the_client_reader_parses()
    {
        var start = new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
        _relations.Rows =
        [
            new OrgRelation(Guid.NewGuid(), OrgRelationKind.ReportsTo, _alice, _bob, null, start, null, _bob, 1),
            new OrgRelation(Guid.NewGuid(), OrgRelationKind.LentTo, _alice, _bob, _project, start, start.AddDays(10), _bob, 2),
        ];

        using var response = await Post("{}");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(_relations.IncludedEnded);

        var model = JsonElement.Parse(text);
        var project = Assert.Single(model.GetProperty("projects").EnumerateArray());
        Assert.Equal(_project.ToString(), project.GetProperty("id").GetString());
        Assert.Equal("Bazaar", project.GetProperty("name").GetString());
        Assert.Equal(["Alice", "Bob"], model.GetProperty("agents").EnumerateArray().Select(a => a.GetProperty("name").GetString()));

        var edges = model.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);

        Assert.Equal("reports_to", edges[0].GetProperty("kind").GetString());
        Assert.Equal(_alice.ToString(), edges[0].GetProperty("fromAgentId").GetString());
        Assert.Equal(_bob.ToString(), edges[0].GetProperty("toAgentId").GetString());
        Assert.Equal(JsonValueKind.Null, edges[0].GetProperty("projectId").ValueKind);
        Assert.Equal(JsonValueKind.Null, edges[0].GetProperty("endsAt").ValueKind);
        Assert.Equal(start, ParseUtc(edges[0].GetProperty("startsAt").GetString()!));

        Assert.Equal("lent_to", edges[1].GetProperty("kind").GetString());
        Assert.Equal(_project.ToString(), edges[1].GetProperty("projectId").GetString());
        Assert.Equal(start.AddDays(10), ParseUtc(edges[1].GetProperty("endsAt").GetString()!));
    }

    [Fact]
    public async Task Empty_body_counts_as_empty_object()
    {
        using var response = await Post(string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"x\"")]
    public async Task Body_that_is_not_an_object_is_400(string body)
    {
        using var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _relations.Calls);
    }

    [Fact]
    public async Task Guard_refusal_is_returned_and_nothing_is_read()
    {
        _refusal = Results.StatusCode(StatusCodes.Status401Unauthorized);

        using var response = await Post("{}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, _guardCalls);
        Assert.Equal(0, _relations.Calls);
    }

    [Fact]
    public async Task Get_has_no_route()
    {
        using var response = await _http.GetAsync("/company/org", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public void WriteModel_without_a_directory_lists_no_projects_or_agents()
    {
        var model = JsonElement.Parse(OrgReadRoute.WriteModel([], [], []));

        Assert.Empty(model.GetProperty("projects").EnumerateArray());
        Assert.Empty(model.GetProperty("agents").EnumerateArray());
        Assert.Empty(model.GetProperty("edges").EnumerateArray());
    }

    private sealed class FakeRelations : IOrgRelationReader
    {
        public IReadOnlyList<OrgRelation> Rows { get; set; } = [];

        public int Calls { get; private set; }

        public bool IncludedEnded { get; private set; }

        public Task<IReadOnlyList<OrgRelation>> ListAllAsync(bool includeEnded, CancellationToken ct = default)
        {
            Calls++;
            IncludedEnded = includeEnded;
            return Task.FromResult(Rows);
        }
    }

    private sealed class FakeDirectory : IOrgDirectory
    {
        public Task<IReadOnlyList<OrgDirectoryEntry>> ListProjectsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgDirectoryEntry>>([new OrgDirectoryEntry(_project, "Bazaar")]);

        public Task<IReadOnlyList<OrgDirectoryEntry>> ListAgentsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OrgDirectoryEntry>>([new OrgDirectoryEntry(_alice, "Alice"), new OrgDirectoryEntry(_bob, "Bob")]);
    }
}
