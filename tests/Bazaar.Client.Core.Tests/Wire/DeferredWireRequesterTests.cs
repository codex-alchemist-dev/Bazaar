using Bazaar.Client.Abstractions.Wire;

namespace Bazaar.Client.Core.Tests.Wire;

public class DeferredWireRequesterTests
{
    [Fact]
    public async Task Unset_requests_fail_with_a_wire_request_exception()
    {
        var deferred = new DeferredWireRequester();

        var ex = await Assert.ThrowsAsync<WireRequestException>(
            () => deferred.RequestAsync<string, string>("x", "p", TestContext.Current.CancellationToken));

        Assert.Equal(DeferredWireRequester.NotConnectedCode, ex.Error.Code);
        Assert.False(deferred.IsAttached);
    }

    [Fact]
    public async Task Set_forwards_requests_and_raises_attached()
    {
        var deferred = new DeferredWireRequester();
        var raised = 0;
        deferred.Attached += (_, _) => raised++;

        deferred.Set(new Echo());
        var reply = await deferred.RequestAsync<string, string>("t", "p", TestContext.Current.CancellationToken);

        Assert.Equal("t:p", reply);
        Assert.True(deferred.IsAttached);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Set_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => new DeferredWireRequester().Set(null!));
    }

    private sealed class Echo : IWireRequester
    {
        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)$"{type}:{payload}");
    }
}
