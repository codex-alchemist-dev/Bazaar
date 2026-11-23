using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals.Http.Tests;

public class HttpApprovalClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }

    private static HttpApprovalClient Client(StubHandler handler, string? token = "tok") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://bazaar.test/") }, () => token);

    private static ApprovalCardDto Card() =>
        new(Guid.NewGuid(), "charles", "pr.merge", "repo#12", "charles asks to run pr.merge.", "{\"pr\":12}", new string('a', 64), [],
            null, DateTimeOffset.UnixEpoch.AddDays(1), ApprovalCardStatus.Pending, [ApprovalDecisionKind.ApproveOnce, ApprovalDecisionKind.Deny]);

    [Fact]
    public async Task Lists_pending_cards_with_the_bearer_credential()
    {
        var card = Card();
        var json = JsonSerializer.Serialize(new[] { card }, ApprovalContractsJsonContext.Default.ApprovalCardDtoArray);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

        var cards = await Client(handler).ListPendingAsync(CancellationToken.None);

        Assert.Equal(card.ApprovalId, Assert.Single(cards).ApprovalId);
        Assert.Equal(("/approvals/pending", "Bearer tok"), (handler.Seen[0].Path, handler.Seen[0].Auth));
    }

    [Fact]
    public async Task A_failed_list_throws_so_the_pane_can_say_so()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<HttpRequestException>(async () => await Client(handler).ListPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Posts_one_decision_to_its_approval_route()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var id = Guid.NewGuid();
        var decision = new ApprovalDecisionDto(id, ApprovalDecisionKind.Deny, "device-1", "c2ln", DateTimeOffset.UnixEpoch);

        await Client(handler).SendAsync(decision, CancellationToken.None);

        var seen = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal($"/approvals/{id:D}/decision", seen.Path);
        Assert.Contains("\"deny\"", seen.Body);
    }

    [Fact]
    public async Task A_refused_send_throws_and_is_not_retried()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var decision = new ApprovalDecisionDto(Guid.NewGuid(), ApprovalDecisionKind.ApproveOnce, "device-1", "c2ln", DateTimeOffset.UnixEpoch);

        await Assert.ThrowsAsync<HttpRequestException>(async () => await Client(handler).SendAsync(decision, CancellationToken.None));
        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task No_credential_sends_no_authorization_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        await Client(handler, token: null).ListPendingAsync(CancellationToken.None);
        Assert.Null(handler.Seen[0].Auth);
    }

    [Fact]
    public void Der_signatures_convert_to_p1363_that_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var data = "payload"u8.ToArray();
        for (var i = 0; i < 50; i++)
        {
            var der = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var p1363 = EcdsaSignatureFormat.DerToP1363(der);
            Assert.Equal(64, p1363.Length);
            Assert.True(key.VerifyData(data, p1363, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }
    }

    [Fact]
    public void Short_values_are_left_padded_and_garbage_is_refused()
    {
        // r = 1, s = 0x80 (needs a sign byte in DER).
        byte[] der = [0x30, 0x07, 0x02, 0x01, 0x01, 0x02, 0x02, 0x00, 0x80];
        var p1363 = EcdsaSignatureFormat.DerToP1363(der);
        Assert.Equal(1, p1363[31]);
        Assert.Equal(0x80, p1363[63]);
        Assert.All(p1363[..31], b => Assert.Equal(0, b));
        Assert.Throws<CryptographicException>(() => EcdsaSignatureFormat.DerToP1363([1, 2, 3]));
    }
}
