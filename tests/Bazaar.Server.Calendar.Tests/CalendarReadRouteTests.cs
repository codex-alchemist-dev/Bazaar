using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Calendar.Tests;

/// <summary>
/// <c>POST /company/calendar</c> on a real loopback Kestrel. The client's <c>CalendarModelReader</c> lives in a client module this
/// server module may not reference, so the shape is asserted field by field against what that reader parses (doc 24 section 5).
/// </summary>
public sealed class CalendarReadRouteTests : IAsyncLifetime
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-0000000000a1");

    private readonly FakeCalendar _calendar = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
    private IResult? _refusal;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync() => await StartAsync(options: null);

    private async Task StartAsync(CalendarReadOptions? options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ICalendarService>(_calendar);
        builder.Services.AddSingleton<TimeProvider>(_time);
        if (options is not null)
            builder.Services.AddSingleton(options);

        _app = builder.Build();
        _app.MapCompanyCalendar(_ => ValueTask.FromResult(_refusal));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<HttpResponseMessage> Post(string body) =>
        await _http.PostAsync("/company/calendar", new StringContent(body, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static DateTimeOffset Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset ParseUtc(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static CalendarEventRecord Record(
        CalendarKind kind, DateTimeOffset starts, DateTimeOffset ends, bool allDay = false, string tz = "UTC",
        CalendarStatus status = CalendarStatus.Confirmed, Guid? agent = null, Guid? project = null, bool availability = false) => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            TitleKeyId = Guid.NewGuid(),
            TitleCiphertext = [1, 2, 3],
            StartsAt = starts,
            EndsAt = ends,
            AllDay = allDay,
            Tz = tz,
            AgentId = agent,
            ProjectId = project,
            AffectsAvailability = availability,
            Status = status,
            Version = 1,
        };

    [Fact]
    public async Task Default_window_is_the_current_month_and_one_month_either_side()
    {
        using var response = await Post("{}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Utc(2026, 10, 1), _calendar.From);
        Assert.Equal(Utc(2027, 1, 1), _calendar.Until);
        Assert.Null(_calendar.AgentId);
    }

    [Fact]
    public async Task Default_window_wraps_the_year()
    {
        _time.SetUtcNow(Utc(2026, 12, 31));

        using var response = await Post("{}");

        Assert.Equal(Utc(2026, 11, 1), _calendar.From);
        Assert.Equal(Utc(2027, 2, 1), _calendar.Until);
    }

    [Fact]
    public async Task From_and_to_are_inclusive_dates()
    {
        using var response = await Post("""{"from":"2026-11-05","to":"2026-11-07"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Utc(2026, 11, 5), _calendar.From);
        Assert.Equal(Utc(2026, 11, 8), _calendar.Until);
    }

    [Fact]
    public async Task A_missing_bound_keeps_the_default_bound()
    {
        using var response = await Post("""{"from":"2026-11-20"}""");

        Assert.Equal(Utc(2026, 11, 20), _calendar.From);
        Assert.Equal(Utc(2027, 1, 1), _calendar.Until);
    }

    [Theory]
    [InlineData("""{"from":"2026-11-08","to":"2026-11-07"}""")]
    [InlineData("""{"from":"11/05/2026"}""")]
    [InlineData("""{"to":5}""")]
    [InlineData("""{"from":"2020-01-01","to":"2026-11-07"}""")]
    [InlineData("[]")]
    [InlineData("nope")]
    public async Task Bad_body_or_window_is_400_and_nothing_is_listed(string body)
    {
        using var response = await Post(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _calendar.Calls);
    }

    [Fact]
    public async Task Empty_body_is_the_default_window()
    {
        using var response = await Post(string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Utc(2026, 10, 1), _calendar.From);
    }

    [Fact]
    public async Task Answers_the_pane_model_the_client_reader_parses()
    {
        var project = Guid.NewGuid();
        var timed = Record(CalendarKind.Release, new DateTimeOffset(2026, 11, 24, 15, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero), project: project, tz: "Europe/Berlin");
        var vacation = Record(CalendarKind.Vacation, Utc(2026, 11, 25), Utc(2026, 11, 28), allDay: true, status: CalendarStatus.Proposed, agent: _agent, availability: true);
        _calendar.Rows = [timed, vacation];
        _calendar.Titles[timed.Id] = "Ship 0.2";
        _calendar.Titles[vacation.Id] = null;

        using var response = await Post("{}");
        var model = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("viewer", model.GetProperty("displayTimezone").GetString());
        var events = model.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(2, events.Length);

        var first = events[0];
        Assert.Equal(timed.Id.ToString(), first.GetProperty("id").GetString());
        Assert.Equal("release", first.GetProperty("kind").GetString());
        Assert.Equal("Ship 0.2", first.GetProperty("title").GetString());
        Assert.Equal(timed.StartsAt, ParseUtc(first.GetProperty("startsAt").GetString()!));
        Assert.Equal(timed.EndsAt, ParseUtc(first.GetProperty("endsAt").GetString()!));
        Assert.False(first.GetProperty("allDay").GetBoolean());
        Assert.Equal("Europe/Berlin", first.GetProperty("tz").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("agentId").ValueKind);
        Assert.Equal(project.ToString(), first.GetProperty("projectId").GetString());
        Assert.False(first.GetProperty("affectsAvailability").GetBoolean());
        Assert.Equal("confirmed", first.GetProperty("status").GetString());

        // All-day: both ends are inclusive calendar dates (the stored end is exclusive), and an erased title still reads as text.
        var second = events[1];
        Assert.Equal("vacation", second.GetProperty("kind").GetString());
        Assert.Equal(CalendarReadRoute.ErasedTitle, second.GetProperty("title").GetString());
        Assert.Equal("2026-11-25", second.GetProperty("startsAt").GetString());
        Assert.Equal("2026-11-27", second.GetProperty("endsAt").GetString());
        Assert.True(second.GetProperty("allDay").GetBoolean());
        Assert.Equal(_agent.ToString(), second.GetProperty("agentId").GetString());
        Assert.True(second.GetProperty("affectsAvailability").GetBoolean());
        Assert.Equal("proposed", second.GetProperty("status").GetString());
    }

    [Fact]
    public async Task All_day_dates_follow_the_events_own_zone()
    {
        // Local midnight 2026-11-25 to 2026-11-26 in Berlin (UTC+1 in November) is 23:00Z the day before.
        var oneDay = Record(CalendarKind.Milestone, new DateTimeOffset(2026, 11, 24, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 25, 23, 0, 0, TimeSpan.Zero), allDay: true, tz: "Europe/Berlin");
        _calendar.Rows = [oneDay];

        using var response = await Post("{}");
        var item = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).GetProperty("events")[0];

        Assert.Equal("2026-11-25", item.GetProperty("startsAt").GetString());
        Assert.Equal("2026-11-25", item.GetProperty("endsAt").GetString());
    }

    [Fact]
    public async Task Cancelled_events_are_listed()
    {
        _calendar.Rows = [Record(CalendarKind.Freeze, Utc(2026, 11, 23), Utc(2026, 11, 24), status: CalendarStatus.Cancelled)];

        using var response = await Post("{}");
        var item = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).GetProperty("events")[0];

        Assert.Equal("cancelled", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Guard_refusal_is_returned_and_nothing_is_listed()
    {
        _refusal = Results.StatusCode(StatusCodes.Status401Unauthorized);

        using var response = await Post("{}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _calendar.Calls);
    }

    [Fact]
    public async Task Get_has_no_route()
    {
        using var response = await _http.GetAsync("/company/calendar", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Display_timezone_follows_the_registered_options()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        await StartAsync(new CalendarReadOptions(DisplayTimezone.Agent));

        using var response = await Post("{}");
        var model = JsonElement.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal("agent", model.GetProperty("displayTimezone").GetString());
    }

    [Fact]
    public void TryResolveWindow_rejects_a_window_over_the_cap()
    {
        Assert.False(CalendarReadRoute.TryResolveWindow(_time.GetUtcNow(), "2026-01-01", "2028-12-31", out _, out _));
        Assert.True(CalendarReadRoute.TryResolveWindow(_time.GetUtcNow(), "2026-01-01", "2026-12-31", out _, out _));
    }

    private sealed class FakeCalendar : ICalendarService
    {
        public IReadOnlyList<CalendarEventRecord> Rows { get; set; } = [];

        public Dictionary<Guid, string?> Titles { get; } = [];

        public int Calls { get; private set; }

        public DateTimeOffset From { get; private set; }

        public DateTimeOffset Until { get; private set; }

        public Guid? AgentId { get; private set; }

        public Task<IReadOnlyList<CalendarEventRecord>> ListOverlappingAsync(DateTimeOffset from, DateTimeOffset until, Guid? agentId, CancellationToken ct = default)
        {
            Calls++;
            (From, Until, AgentId) = (from, until, agentId);
            return Task.FromResult(Rows);
        }

        public ValueTask<string?> OpenTitleAsync(CalendarEventRecord record, CancellationToken ct = default) =>
            ValueTask.FromResult(Titles.GetValueOrDefault(record.Id));

        public Task<CalendarResult> CreateAsync(CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarResult> UpdateAsync(Guid id, long expectedVersion, CalendarDraft draft, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarResult> ConfirmAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarResult> CancelAsync(Guid id, long expectedVersion, CalendarActor actor, Guid noticeGcId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CalendarEventRecord?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
