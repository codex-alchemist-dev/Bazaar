using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Gc;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Gc.Tests;

public sealed class GcListSourceTests
{
    private sealed class FakeRequester(GcListReply reply) : IWireRequester
    {
        public string? Type { get; private set; }

        public GcListRequest? Sent { get; private set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Type = type;
            Sent = payload as GcListRequest;
            return Task.FromResult((TResponse)(object)reply);
        }
    }

    [Fact]
    public async Task Sends_gc_list_and_returns_the_reply()
    {
        var gc = Guid.CreateVersion7();
        var reply = new GcListReply([new GcListItem(gc, "general", "general", 2, 5, null)], false, gc);
        var requester = new FakeRequester(reply);

        var result = await new GcListSource(requester).ListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("gc.list", requester.Type);
        Assert.Equal(new GcListRequest(GcListSource.DefaultLimit, false), requester.Sent);
        Assert.Same(reply, result);
        Assert.Equal(gc, result.DefaultGcId);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(50, 50)]
    [InlineData(5000, 200)]
    public async Task Clamps_the_limit_to_what_the_server_accepts(int asked, int sent)
    {
        var requester = new FakeRequester(new GcListReply([], false, null));

        await new GcListSource(requester).ListAsync(asked, includeThinking: true, TestContext.Current.CancellationToken);

        Assert.Equal(new GcListRequest(sent, true), requester.Sent);
    }
}
