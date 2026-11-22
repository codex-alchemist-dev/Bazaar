using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bazaar.Server.Council.Escalation;
using Bazaar.Server.Council.Http;
using Bazaar.Server.Council.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Council.Http.Tests;

public sealed class CouncilEscalationRoutesTests : IAsyncLifetime
{
    private static readonly Guid _voteId = Guid.NewGuid();
    private readonly FakeCouncil _council = new();
    private WebApplication? _app;
    private HttpClient _client = new();

    private sealed class FakeCouncil : ICouncilService, ICouncilEscalationSource, ICouncilStore
    {
        public CouncilVoteRecord? Vote { get; set; }

        public List<(string Choice, string? Reasoning)> Resolved { get; } = [];

        public Task<IReadOnlyList<Guid>> ListEscalatedAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(Vote is { State: VoteState.Escalated } v ? [v.VoteId] : []);

        public Task<CouncilVoteRecord?> FindVoteAsync(Guid voteId, CancellationToken ct) => Task.FromResult(Vote);

        public Task<IReadOnlyList<CouncilTransitionRecord>> ListTransitionsAsync(Guid voteId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CouncilTransitionRecord>>(
                [new CouncilTransitionRecord(2, VoteState.Escalated, "escalated: tie", DateTimeOffset.UnixEpoch)]);

        public Task<IReadOnlyList<StoredBallot>> ListBallotsAsync(Guid voteId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StoredBallot>>([]);

        public Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct)
        {
            if (optionChoice == "bogus")
            {
                throw new CouncilRejectedException("bad");
            }

            Resolved.Add((optionChoice, reasoning));
            Vote = Vote! with { State = VoteState.Decided };
            return Task.CompletedTask;
        }

        public Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct) => throw new NotSupportedException();

        public Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct) => throw new NotSupportedException();

        public Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct) => throw new NotSupportedException();

        public Task RedriveDecisionAsync(Guid voteId, CancellationToken ct) => throw new NotSupportedException();

        public Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct) => throw new NotSupportedException();

        public Task InsertVoteAsync(CouncilVoteRecord vote, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> TryTransitionAsync(Guid voteId, long expectedVersion, VoteState target, string reason, DateTimeOffset at, DateTimeOffset? newDeadlineAt, int? newExtensions, CancellationToken ct) => throw new NotSupportedException();

        public Task SetVaultDecisionNoAsync(Guid voteId, int decisionNo, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListVotesDueAsync(DateTimeOffset now, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListDecidedSinceAsync(DateTimeOffset since, int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListUndeliveredMembershipDecisionsAsync(int limit, CancellationToken ct) => throw new NotSupportedException();

        public Task MarkMembershipOutcomeDeliveredAsync(Guid voteId, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> TryInsertBallotAsync(StoredBallot ballot, CancellationToken ct) => throw new NotSupportedException();
    }

    public async ValueTask InitializeAsync()
    {
        _council.Vote = new CouncilVoteRecord(
            _voteId, Guid.NewGuid(), "task/42", VoteKind.ApproveReject, MotionClass.Ordinary, VoteState.Escalated, 3, [],
            DateTimeOffset.UnixEpoch, 0, new HashSet<string> { "cal", "dot" }, "ada", null, 1, null, DateTimeOffset.UnixEpoch);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICouncilService>(_council);
        builder.Services.AddSingleton<ICouncilStore>(_council);
        builder.Services.AddSingleton<ICouncilEscalationSource>(_council);
        builder.Services.AddSingleton<CouncilEscalationService>();
        _app = builder.Build();
        _app.MapCouncilEscalations((http, run) =>
        {
            var kind = http.Request.Headers["X-Kind"].ToString();
            return kind.Length == 0 ? Task.FromResult(Results.StatusCode(401)) : run(http, Guid.NewGuid(), kind);
        });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? kind, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (kind is not null)
        {
            req.Headers.Add("X-Kind", kind);
        }

        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(req, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Owner_lists_the_escalated_votes()
    {
        var res = await SendAsync(HttpMethod.Get, "/council/escalations", "owner");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var row = doc.RootElement.GetProperty("escalations")[0];
        Assert.Equal(_voteId, row.GetProperty("voteId").GetGuid());
        Assert.Equal("yes", row.GetProperty("options")[0].GetString());
        Assert.Equal("escalated: tie", row.GetProperty("escalationReason").GetString());
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("human")]
    public async Task A_non_owner_is_refused_on_list_and_resolve(string kind)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/council/escalations", kind)).StatusCode);
        var res = await SendAsync(HttpMethod.Post, $"/council/escalations/{_voteId}/resolve", kind, new { choice = "yes" });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_council.Resolved);
    }

    [Fact]
    public async Task Owner_resolves_with_choice_and_reasoning()
    {
        var res = await SendAsync(HttpMethod.Post, $"/council/escalations/{_voteId}/resolve", "owner", new { choice = "no", reasoning = "Because." });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(("no", "Because."), Assert.Single(_council.Resolved));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, $"/council/escalations/{_voteId}/resolve", "owner", new { choice = "no" })).StatusCode);
    }

    [Fact]
    public async Task A_bad_choice_is_422_and_a_missing_choice_is_400()
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await SendAsync(HttpMethod.Post, $"/council/escalations/{_voteId}/resolve", "owner", new { choice = "bogus" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, $"/council/escalations/{_voteId}/resolve", "owner", new { reasoning = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(HttpMethod.Post, "/council/escalations/not-a-guid/resolve", "owner", new { choice = "yes" })).StatusCode);
    }
}
