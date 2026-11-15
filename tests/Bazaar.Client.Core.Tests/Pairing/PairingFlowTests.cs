using System.Net;
using System.Text;
using Bazaar.Client.Core.Pairing;

namespace Bazaar.Client.Core.Tests.Pairing;

public class PairingFlowTests
{
    private static readonly Uri _base = new("http://pair.test:8080/");

    [Fact]
    public async Task Successful_pairing_saves_only_the_refresh_token_and_returns_the_grant()
    {
        var store = new InMemoryRefreshCredentialStore();
        var handler = new ScriptedPairingHandler(PairingFakes.DeviceAuthorized(), PairingFakes.TokenGranted());

        var grant = await PairingFakes.Flow(store, handler).PairAsync(_base, "ABCD2345", "Dom laptop", "windows", TestContext.Current.CancellationToken);

        Assert.Equal(new PairingGrant("access-test", "refresh-test", 900), grant);
        Assert.Equal("refresh-test", store.Value);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task The_user_code_is_normalised_before_it_is_sent()
    {
        var handler = new ScriptedPairingHandler(PairingFakes.DeviceAuthorized(), PairingFakes.TokenGranted());

        await PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler)
            .PairAsync(_base, " ab-cd 12 ", "Dom laptop", "windows", TestContext.Current.CancellationToken);

        Assert.Contains("\"user_code\":\"ABCD12\"", handler.Bodies[0]);
    }

    [Theory]
    [InlineData("ftp://pair.test/")]
    [InlineData("mailto:someone@example.test")]
    public async Task A_base_address_that_is_not_http_or_https_is_rejected(string address)
    {
        var flow = PairingFakes.Flow(new InMemoryRefreshCredentialStore(), new ScriptedPairingHandler());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            flow.PairAsync(new Uri(address), "ABCD2345", "Dom laptop", "windows", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_code_that_is_empty_after_normalising_is_rejected_before_any_request()
    {
        var handler = new ScriptedPairingHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler).PairAsync(_base, " - ", "Dom laptop", "windows", TestContext.Current.CancellationToken));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Expired_token_propagates_as_a_pairing_exception_and_saves_nothing()
    {
        var store = new InMemoryRefreshCredentialStore();
        var handler = new ScriptedPairingHandler(PairingFakes.DeviceAuthorized(), _ => PairingFakes.Json(HttpStatusCode.BadRequest, """{"error":"expired_token"}"""));

        var error = await Assert.ThrowsAsync<PairingException>(() =>
            PairingFakes.Flow(store, handler).PairAsync(_base, "ABCD2345", "Dom laptop", "windows", TestContext.Current.CancellationToken));

        Assert.Equal("expired_token", error.Code);
        Assert.Equal(0, store.SaveCount);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task Authorization_pending_is_polled_again_until_the_grant_arrives()
    {
        var handler = new ScriptedPairingHandler(
            PairingFakes.DeviceAuthorized(),
            _ => PairingFakes.Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            PairingFakes.TokenGranted());

        var grant = await PairingFakes.Flow(new InMemoryRefreshCredentialStore(), handler)
            .PairAsync(_base, "ABCD2345", "Dom laptop", "windows", TestContext.Current.CancellationToken);

        Assert.Equal("refresh-test", grant.RefreshToken);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task IsPaired_is_false_until_a_pairing_saves_a_refresh_credential()
    {
        var store = new InMemoryRefreshCredentialStore();
        var flow = PairingFakes.Flow(store, new ScriptedPairingHandler(PairingFakes.DeviceAuthorized(), PairingFakes.TokenGranted()));

        Assert.False(await flow.IsPairedAsync(TestContext.Current.CancellationToken));

        await flow.PairAsync(_base, "ABCD2345", "Dom laptop", "windows", TestContext.Current.CancellationToken);

        Assert.True(await flow.IsPairedAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_constructor_rejects_null_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new PairingFlow(null!, _ => new PairingClient(new HttpClient(), (_, _) => Task.CompletedTask)));
        Assert.Throws<ArgumentNullException>(() => new PairingFlow(new InMemoryRefreshCredentialStore(), null!));
    }

    /// <summary>In-memory stand-in for the device's refresh credential store. Counts saves so tests can prove nothing else was written.</summary>
    internal sealed class InMemoryRefreshCredentialStore : IRefreshCredentialStore
    {
        public string? Value { get; private set; }

        public int SaveCount { get; private set; }

        public int ClearCount { get; private set; }

        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);

        public Task SaveAsync(string refreshCredential, CancellationToken cancellationToken = default)
        {
            Value = refreshCredential;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Value = null;
            ClearCount++;
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers each request with the next scripted response and records what was sent.</summary>
    internal sealed class ScriptedPairingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private int _next;

        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return script[_next++](request);
        }
    }

    /// <summary>Holds every request until the test opens the gate, then answers with the scripted responses in order.</summary>
    internal sealed class GatedPairingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _next;

        public int Calls { get; private set; }

        public void Open() => _gate.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return script[_next++](request);
        }
    }

    /// <summary>Shared fixtures for the pairing tests: the flow under test and canned server answers with obvious fake values.</summary>
    internal static class PairingFakes
    {
        public static PairingFlow Flow(IRefreshCredentialStore store, HttpMessageHandler handler) =>
            new(store, uri => new PairingClient(new HttpClient(handler) { BaseAddress = uri }, (_, _) => Task.CompletedTask));

        public static Func<HttpRequestMessage, HttpResponseMessage> DeviceAuthorized() =>
            _ => Json(HttpStatusCode.OK, """{"device_code":"device-test","interval":0}""");

        public static Func<HttpRequestMessage, HttpResponseMessage> TokenGranted() =>
            _ => Json(HttpStatusCode.OK, """{"access_token":"access-test","refresh_token":"refresh-test","expires_in":900}""");

        public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
