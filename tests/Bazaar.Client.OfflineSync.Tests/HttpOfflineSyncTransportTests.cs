using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class HttpOfflineSyncTransportTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, Body);
        }
    }

    private static HttpOfflineSyncTransport Transport(StubHandler handler, string? token = "tok") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://bazaar.test/") }, () => token);

    private static OfflineWriteBatch Batch() =>
        new(OfflineTestData.Device, [OfflineTestData.Write(OfflineTestData.Device, 1, "hello")]);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Posts_the_batch_with_bearer_token_and_returns_the_result()
    {
        var handler = new StubHandler((_, body) =>
        {
            var request = JsonSerializer.Deserialize(body!, OfflineSyncWireContext.Default.OfflineBatchRequestDto)!;
            var outcomes = request.Writes.Select(w => new OfflineOutcomeDto(w.WriteId, OfflineOutcomeKind.Applied, "1", null)).ToList();
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new OfflineBatchResponseDto(outcomes, 1, null), OfflineSyncWireContext.Default.OfflineBatchResponseDto));
        });
        var batch = Batch();

        var result = await Transport(handler).SendAsync(batch, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://bazaar.test/offline-sync/batch", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.Request.Headers.Authorization.Parameter);
        Assert.Equal(1, result.AckedSequence);
        Assert.Equal(batch.Writes[0].WriteId, Assert.Single(result.Outcomes).WriteId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Non_ok_answer_throws_with_the_status(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Json(status, "{\"error\":\"x\"}"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Transport(handler).SendAsync(Batch(), CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    public async Task Malformed_answer_throws(string body)
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, body));

        await Assert.ThrowsAsync<HttpRequestException>(() => Transport(handler).SendAsync(Batch(), CancellationToken.None));
    }

    [Fact]
    public async Task Sends_no_authorization_header_without_a_credential()
    {
        var handler = new StubHandler((_, _) => Json(HttpStatusCode.OK, "{\"outcomes\":[],\"acked_sequence\":0,\"gap_at_sequence\":null}"));

        await Transport(handler, token: null).SendAsync(Batch(), CancellationToken.None);

        Assert.Null(handler.Request!.Headers.Authorization);
    }
}
