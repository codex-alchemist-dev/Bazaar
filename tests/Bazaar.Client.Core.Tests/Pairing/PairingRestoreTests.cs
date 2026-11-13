using System.Net;
using Bazaar.Client.Core.Pairing;
using static Bazaar.Client.Core.Tests.Pairing.PairingFlowTests;

namespace Bazaar.Client.Core.Tests.Pairing;

public class PairingRestoreTests
{
    private static readonly Uri _base = new("http://pair.test:8080/");

    [Fact]
    public async Task Restore_without_a_stored_credential_returns_null_and_makes_no_request()
    {
        var handler = new ScriptedPairingHandler();

        var grant = await PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler)
            .RestoreAsync(_base, "android", "bazaar-android", TestContext.Current.CancellationToken);

        Assert.Null(grant);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Restore_success_saves_the_rotated_refresh_token_and_returns_the_grant()
    {
        var store = new InMemoryRefreshCredentialStore();
        await store.SaveAsync("refresh-test", TestContext.Current.CancellationToken);
        var handler = new ScriptedPairingHandler(_ => PairingFakes.Json(HttpStatusCode.OK,
            """{"access_token":"access-test","token_type":"Bearer","expires_in":900,"refresh_token":"refresh-rotated-test"}"""));

        var grant = await PairingFakes.Flow(store, handler)
            .RestoreAsync(_base, "android", "bazaar-android", TestContext.Current.CancellationToken);

        Assert.Equal(new PairingGrant("access-test", "refresh-rotated-test", 900), grant);
        Assert.Equal("refresh-rotated-test", store.Value);
        Assert.Equal("/auth/refresh", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("\"refresh_token\":\"refresh-test\"", handler.Bodies[0]);
        Assert.Contains("\"device_class\":\"android\"", handler.Bodies[0]);
        Assert.Contains("\"client_build\":\"bazaar-android\"", handler.Bodies[0]);
    }

    [Fact]
    public async Task Invalid_grant_returns_null_and_clears_the_stored_credential()
    {
        var store = new InMemoryRefreshCredentialStore();
        await store.SaveAsync("refresh-test", TestContext.Current.CancellationToken);
        var handler = new ScriptedPairingHandler(_ => PairingFakes.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_grant"}"""));

        var grant = await PairingFakes.Flow(store, handler)
            .RestoreAsync(_base, "android", "bazaar-android", TestContext.Current.CancellationToken);

        Assert.Null(grant);
        Assert.Null(store.Value);
        Assert.Equal(1, store.ClearCount);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Transport_failure_propagates_and_keeps_the_stored_credential()
    {
        var store = new InMemoryRefreshCredentialStore();
        await store.SaveAsync("refresh-test", TestContext.Current.CancellationToken);
        var handler = new ScriptedPairingHandler(_ => throw new HttpRequestException("server unreachable"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            PairingFakes.Flow(store, handler).RestoreAsync(_base, "android", "bazaar-android", TestContext.Current.CancellationToken));

        Assert.Equal("refresh-test", store.Value);
        Assert.Equal(0, store.ClearCount);
    }

    [Fact]
    public async Task TryRestore_returns_false_and_does_not_throw_when_the_server_is_unreachable()
    {
        var store = new InMemoryRefreshCredentialStore();
        await store.SaveAsync("refresh-test", TestContext.Current.CancellationToken);
        var handler = new ScriptedPairingHandler(_ => throw new HttpRequestException("server unreachable"));
        var view = new PairingViewModel(PairingFakes.Flow(store, handler), "android") { ServerAddress = "http://pair.test:8080/" };

        var restored = await view.TryRestoreAsync(TestContext.Current.CancellationToken);

        Assert.False(restored);
        Assert.Equal(PairingStatus.Idle, view.Status);
        Assert.Equal("Could not reach the server.", view.Message);
        Assert.Equal("refresh-test", store.Value);
    }
}
