using System.Net;
using System.Text;
using System.Text.Json;

namespace Bazaar.Client.Council.Tests;

public class HttpCouncilEscalationClientTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    private static HttpCouncilEscalationClient Client(Stub handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://bazaar.test/") }, () => "tok");

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Lists_escalations_with_dissent_and_taint_and_the_bearer_credential()
    {
        var id = Guid.NewGuid();
        var json = $$$"""
            {"escalations":[{"voteId":"{{{id}}}","gcId":"{{{Guid.NewGuid()}}}","subject":"task/42","kind":"ApproveReject","motionClass":"Ordinary",
            "options":["yes","no"],"eligibleCount":3,"independentBallotCount":2,"ballotsRevealed":true,
            "ballots":[{"voterId":"cal","phase":"Independent","choice":"yes","reasoning":"r","tainted":true}],
            "escalationReason":"escalated: tie","escalatedAt":"2026-11-20T10:00:00+00:00","deadlineAt":"2026-11-20T09:00:00+00:00","ageSeconds":60,
            "dissent":{"independent":[{"choice":"yes","count":1},{"choice":"no","count":1}],"followUp":[],"leadingChoice":null,"dissentingBallots":2,"nonAbstainingBallots":2,"dissentShare":1.0},
            "taint":{"anyTainted":true,"taintedBallots":1,"totalBallots":2,"taintedVoters":["cal"]}}]}
            """;
        var handler = new Stub(_ => Json(HttpStatusCode.OK, json));

        var cards = await Client(handler).ListEscalationsAsync(TestContext.Current.CancellationToken);

        var card = Assert.Single(cards);
        Assert.Equal(id, card.VoteId);
        Assert.Null(card.Dissent!.LeadingChoice);
        Assert.Equal(1.0, card.Dissent.DissentShare);
        Assert.True(card.Taint.AnyTainted);
        Assert.Equal(["cal"], card.Taint.TaintedVoters);
        Assert.True(card.Ballots[0].Tainted);
        Assert.Equal(("/council/escalations", "Bearer tok"), (handler.Seen[0].Path, handler.Seen[0].Auth));
    }

    [Fact]
    public async Task An_unrevealed_vote_has_no_dissent()
    {
        var json = $$$"""{"escalations":[{"voteId":"{{{Guid.NewGuid()}}}","gcId":"{{{Guid.NewGuid()}}}","subject":"s","kind":"ApproveReject","motionClass":"Ordinary","options":["yes","no"],"eligibleCount":3,"independentBallotCount":1,"ballotsRevealed":false,"ballots":[],"escalationReason":null,"escalatedAt":"2026-11-20T10:00:00+00:00","deadlineAt":"2026-11-20T09:00:00+00:00","ageSeconds":0,"dissent":null,"taint":{"anyTainted":false,"taintedBallots":0,"totalBallots":1,"taintedVoters":[]}}]}""";
        var cards = await Client(new Stub(_ => Json(HttpStatusCode.OK, json))).ListEscalationsAsync(TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(cards).Dissent);
    }

    [Fact]
    public async Task A_forbidden_list_throws_with_the_status_and_code()
    {
        var handler = new Stub(_ => Json(HttpStatusCode.Forbidden, """{"error":"forbidden"}"""));

        var ex = await Assert.ThrowsAsync<CouncilEscalationException>(async () => await Client(handler).ListEscalationsAsync(TestContext.Current.CancellationToken));

        Assert.Equal((403, "forbidden"), (ex.StatusCode, ex.Code));
    }

    [Fact]
    public async Task Resolves_by_posting_choice_and_reasoning_to_the_vote_route()
    {
        var id = Guid.NewGuid();
        var handler = new Stub(_ => Json(HttpStatusCode.OK, $$"""{"voteId":"{{id}}","choice":"no"}"""));

        await Client(handler).ResolveEscalationAsync(id, "no", "Because.", TestContext.Current.CancellationToken);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal($"/council/escalations/{id}/resolve", seen.Path);
        using var doc = JsonDocument.Parse(seen.Body);
        Assert.Equal("no", doc.RootElement.GetProperty("choice").GetString());
        Assert.Equal("Because.", doc.RootElement.GetProperty("reasoning").GetString());
    }

    [Fact]
    public async Task A_refused_resolve_throws_and_is_not_retried()
    {
        var handler = new Stub(_ => Json(HttpStatusCode.NotFound, """{"error":"not_escalated"}"""));

        var ex = await Assert.ThrowsAsync<CouncilEscalationException>(async () => await Client(handler).ResolveEscalationAsync(Guid.NewGuid(), "yes", null, TestContext.Current.CancellationToken));

        Assert.Equal((404, "not_escalated"), (ex.StatusCode, ex.Code));
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task A_non_json_failure_still_throws_without_a_code()
    {
        var handler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<CouncilEscalationException>(async () => await Client(handler).ResolveEscalationAsync(Guid.NewGuid(), "yes", null, TestContext.Current.CancellationToken));

        Assert.Equal((502, null), (ex.StatusCode, ex.Code));
    }
}
