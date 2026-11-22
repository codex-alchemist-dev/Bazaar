using System.Net;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Approvals.Tests;

public sealed class HttpDraftReviewDecisionSenderTests
{
    private static readonly Guid DraftId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("unreachable");
    }

    private static DraftReviewDecisionDto Decision(string? signature = "c2ln") => new(
        DraftId,
        new ApprovalDecisionDto(Guid.NewGuid(), ApprovalDecisionKind.EditThenApprove, "phone-1", signature, DateTimeOffset.UnixEpoch),
        "edited body");

    private static ServerEndpoint Endpoint() => new(new Uri("http://127.0.0.1:8080/"), "tok");

    [Fact]
    public async Task Posts_the_decision_to_the_draft_route_with_credential()
    {
        var handler = new Handler(HttpStatusCode.NoContent);
        var sut = HttpDraftReviewDecisionSender.Create(Endpoint(), handler);
        var decision = Decision();

        await sut.SendAsync(decision, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal($"http://127.0.0.1:8080/community/drafts/{DraftId:D}/decision", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.Request.Headers.Authorization.Parameter);
        var echoed = JsonSerializer.Deserialize(handler.Body!, CommunityContractsJsonContext.Default.DraftReviewDecisionDto)!;
        Assert.Equal(decision.DraftId, echoed.DraftId);
        Assert.Equal("edited body", echoed.EditedBody);
        Assert.Equal("c2ln", echoed.Decision.SignatureBase64);
        Assert.Equal(ApprovalDecisionKind.EditThenApprove, echoed.Decision.Decision);
    }

    [Fact]
    public async Task Unsigned_decision_is_never_sent()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var sut = HttpDraftReviewDecisionSender.Create(Endpoint(), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SendAsync(Decision(signature: null), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SendAsync(Decision(signature: ""), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Server_refusal_surfaces_as_an_exception(HttpStatusCode status)
    {
        var sut = HttpDraftReviewDecisionSender.Create(Endpoint(), new Handler(status));
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.SendAsync(Decision(), CancellationToken.None));
    }

    [Fact]
    public async Task Unreachable_server_surfaces_as_an_exception()
    {
        var sut = HttpDraftReviewDecisionSender.Create(Endpoint(), new ThrowingHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.SendAsync(Decision(), CancellationToken.None));
    }

    [Fact]
    public void Route_constant_matches_the_documented_template()
    {
        Assert.Equal("community/drafts/{id}/decision", CommunityRoutes.DraftDecisionTemplate);
        Assert.Equal($"community/drafts/{DraftId:D}/decision", CommunityRoutes.DraftDecision(DraftId));
    }
}
