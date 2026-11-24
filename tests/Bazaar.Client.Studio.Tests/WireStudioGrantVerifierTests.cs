using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Engines;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Studio.Tests;

public sealed class WireStudioGrantVerifierTests
{
    private sealed class Requester(Func<object, object> answer) : IWireRequester
    {
        public string? Type { get; private set; }

        public object? Sent { get; private set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Type = type;
            Sent = payload;
            return Task.FromResult((TResponse)answer(payload!));
        }
    }

    [Fact]
    public async Task It_sends_the_grant_tool_and_risk_class_and_returns_the_servers_answer()
    {
        var requester = new Requester(_ => new EngineGrantVerifyReply(true));

        bool covers = await new WireStudioGrantVerifier(requester).CoversAsync("g1", "multi_edit", RiskClass.Write, TestContext.Current.CancellationToken);

        Assert.True(covers);
        Assert.Equal(EngineRelayWire.GrantVerifyType, requester.Type);
        Assert.Equal(new EngineGrantVerifyRequest("g1", "multi_edit", RiskClass.Write), requester.Sent);
    }

    [Fact]
    public async Task A_no_from_the_server_is_false()
    {
        var verifier = new WireStudioGrantVerifier(new Requester(_ => new EngineGrantVerifyReply(false)));

        Assert.False(await verifier.CoversAsync("g1", "multi_edit", RiskClass.Write, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_wire_error_is_thrown_so_the_bridge_fails_closed()
    {
        var verifier = new WireStudioGrantVerifier(new Requester(_ => throw new WireRequestException(new WireError(WireErrorCodes.Unauthorized, null, null))));

        await Assert.ThrowsAsync<WireRequestException>(() => verifier.CoversAsync("g1", "multi_edit", RiskClass.Write, TestContext.Current.CancellationToken));
    }
}
