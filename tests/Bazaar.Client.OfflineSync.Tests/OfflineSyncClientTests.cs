using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Client.OfflineSync;
using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync.Tests;

public sealed class OfflineSyncClientTests : IDisposable
{
    private readonly TempQueueDirectory _dir = new();
    private readonly InMemoryOfflineQueueAnchorStore _anchors = new();
    private readonly List<OfflineSyncClient> _clients = [];

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _dir.Dispose();
    }

    [Fact]
    public async Task EnqueueThenSync_DrainsThroughTheHttpTransport()
    {
        var handler = new EchoHandler(Ack);
        var client = Open(handler);

        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var enqueued = await client.EnqueueAsync(OfflineTestData.Write(OfflineTestData.Device, sequence, "write " + sequence), TestContext.Current.CancellationToken);
            Assert.Equal(EnqueueStatus.Accepted, enqueued.Status);
        }

        var result = await client.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Equal(3, result.AckedSequence);
        Assert.Equal(0, result.AttentionCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(OfflineTestData.Device, request.DeviceId);
        Assert.Equal(3, request.Writes.Count);
        Assert.IsType<ConnectionState.Live>(client.State.Current);
        Assert.Equal(OfflineTestData.Device, client.DeviceId);
    }

    [Fact]
    public async Task TransportFailure_KeepsTheWritesQueued_AndSurvivesARestart()
    {
        var client = Open(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        await client.EnqueueAsync(OfflineTestData.Write(OfflineTestData.Device, 1, "offline edit"), TestContext.Current.CancellationToken);

        var offline = await client.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OfflineReplayStatus.Offline, offline.Status);
        Assert.IsType<ConnectionState.Offline>(client.State.Current);
        client.Dispose();

        var reopened = Open(new EchoHandler(Ack));
        var drained = await reopened.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OfflineReplayStatus.Drained, drained.Status);
        Assert.Equal(1, drained.AckedSequence);
    }

    [Fact]
    public async Task TornTail_IsRepairedOnOpen_AndReportedOnTheNextRun()
    {
        var first = Open(new EchoHandler(Ack));
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            await first.EnqueueAsync(OfflineTestData.Write(OfflineTestData.Device, sequence, "write " + sequence), TestContext.Current.CancellationToken);
        }

        first.Dispose();
        var file = Path.Combine(_dir.Root, EncryptedOfflineQueue.FileName);
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..^5]);

        var reopened = Open(new EchoHandler(Ack));
        var result = await reopened.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OfflineReplayStatus.Drained, result.Status);
        Assert.Equal(1, result.RepairedLostWrites);
        Assert.Equal(1, result.AttentionCount);
        Assert.Equal(new ConnectionState.Attention(1), reopened.State.Current);
    }

    [Fact]
    public async Task OverlappingSync_IsRefused_WhileARunIsInProgress()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Open(new BlockingHandler(release));
        await client.EnqueueAsync(OfflineTestData.Write(OfflineTestData.Device, 1, "held"), TestContext.Current.CancellationToken);

        var first = client.SyncAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SyncAsync(TestContext.Current.CancellationToken));
        release.SetResult();
        var result = await first;
        Assert.Equal(OfflineReplayStatus.Offline, result.Status);
    }

    [Fact]
    public async Task AfterDispose_EnqueueAndSyncAreRefused()
    {
        var client = Open(new EchoHandler(Ack));
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.EnqueueAsync(OfflineTestData.Write(OfflineTestData.Device, 1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SyncAsync(TestContext.Current.CancellationToken));
    }

    private OfflineSyncClient Open(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://bazaar.test/") };
        var client = OfflineSyncClientFactory.Create(http, () => "tok", _dir.Root, OfflineTestData.Key(), _anchors, OfflineTestData.Device);
        _clients.Add(client);
        return client;
    }

    private static HttpResponseMessage Ack(OfflineBatchRequestDto request)
    {
        var outcomes = request.Writes.Select(w => new OfflineOutcomeDto(w.WriteId, OfflineOutcomeKind.Applied, "1", null)).ToList();
        var last = request.Writes.Count == 0 ? 0 : request.Writes[^1].DeviceSequence;
        var json = JsonSerializer.Serialize(new OfflineBatchResponseDto(outcomes, last, null), OfflineSyncWireContext.Default.OfflineBatchResponseDto);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class EchoHandler(Func<OfflineBatchRequestDto, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<OfflineBatchRequestDto> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var dto = JsonSerializer.Deserialize(body, OfflineSyncWireContext.Default.OfflineBatchRequestDto)!;
            Requests.Add(dto);
            return respond(dto);
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class BlockingHandler(TaskCompletionSource release) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
}
