using System.Net;
using Bazaar.Client.Core.Pairing;
using static Bazaar.Client.Core.Tests.Pairing.PairingFlowTests;

namespace Bazaar.Client.Core.Tests.Pairing;

public class PairingViewModelTests
{
    [Fact]
    public void CanPair_needs_a_code_and_a_valid_server_address()
    {
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), new ScriptedPairingHandler()), "windows");

        Assert.False(model.CanPair);

        model.UserCode = "   ";
        Assert.False(model.CanPair);

        model.UserCode = "ABCD2345";
        Assert.True(model.CanPair);

        model.ServerAddress = "not a url";
        Assert.False(model.CanPair);
    }

    [Fact]
    public async Task A_successful_pairing_sets_Paired_and_raises_Paired_with_the_grant()
    {
        var store = new InMemoryRefreshCredentialStore();
        var handler = new ScriptedPairingHandler(PairingFakes.DeviceAuthorized(), PairingFakes.TokenGranted());
        var model = new PairingViewModel(PairingFakes.Flow(store, handler), "windows") { UserCode = "ABCD2345" };
        PairingCompletedEventArgs? completed = null;
        model.Paired += (_, args) => completed = args;

        await model.PairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PairingStatus.Paired, model.Status);
        Assert.Equal("Paired.", model.Message);
        Assert.False(model.CanPair);
        Assert.NotNull(completed);
        Assert.Equal(new PairingGrant("access-test", "refresh-test", 900), completed.Grant);
        Assert.Equal(new Uri("http://127.0.0.1:8080/"), completed.BaseAddress);
        Assert.Equal("refresh-test", store.Value);
    }

    [Fact]
    public async Task An_expired_code_sets_Failed_with_the_expired_message()
    {
        var handler = new ScriptedPairingHandler(
            PairingFakes.DeviceAuthorized(),
            _ => PairingFakes.Json(HttpStatusCode.BadRequest, """{"error":"expired_token"}"""));
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler), "windows") { UserCode = "ABCD2345" };

        await model.PairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PairingStatus.Failed, model.Status);
        Assert.Equal("That code expired. Ask for a new one.", model.Message);
        Assert.True(model.CanPair);
    }

    [Fact]
    public async Task An_unknown_server_error_shows_its_code()
    {
        var handler = new ScriptedPairingHandler(
            PairingFakes.DeviceAuthorized(),
            _ => PairingFakes.Json(HttpStatusCode.BadRequest, """{"error":"server_busy"}"""));
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler), "windows") { UserCode = "ABCD2345" };

        await model.PairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PairingStatus.Failed, model.Status);
        Assert.Equal("Pairing failed (server_busy).", model.Message);
    }

    [Fact]
    public async Task A_transport_failure_sets_Failed_and_does_not_throw()
    {
        var handler = new ScriptedPairingHandler(_ => throw new HttpRequestException("connection refused"));
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler), "windows") { UserCode = "ABCD2345" };

        await model.PairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PairingStatus.Failed, model.Status);
        Assert.Equal("Could not reach the server.", model.Message);
    }

    [Fact]
    public async Task A_second_PairAsync_while_pairing_is_ignored()
    {
        var handler = new GatedPairingHandler(
            PairingFakes.DeviceAuthorized(),
            _ => PairingFakes.Json(HttpStatusCode.BadRequest, """{"error":"access_denied"}"""));
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler), "windows") { UserCode = "ABCD2345" };

        var first = model.PairAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PairingStatus.Pairing, model.Status);

        await model.PairAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(PairingStatus.Pairing, model.Status);

        handler.Open();
        await first;

        Assert.Equal(2, handler.Calls);

        Assert.Equal(PairingStatus.Failed, model.Status);
        Assert.Equal("The request was denied on your other device.", model.Message);
    }

    [Fact]
    public async Task Cancel_ends_an_attempt_in_flight_with_a_cancelled_message()
    {
        var handler = new GatedPairingHandler();
        var model = new PairingViewModel(PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler), "windows") { UserCode = "ABCD2345" };

        var first = model.PairAsync(TestContext.Current.CancellationToken);
        model.Cancel();
        await first;

        Assert.Equal(PairingStatus.Failed, model.Status);
        Assert.Equal("Pairing was cancelled.", model.Message);
    }
}
