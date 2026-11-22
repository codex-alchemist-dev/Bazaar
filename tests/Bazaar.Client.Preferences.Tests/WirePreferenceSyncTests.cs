using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Preferences.Abstractions;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Preferences.Tests;

public class WirePreferenceSyncTests
{
    private sealed class Wire : IWireRequester
    {
        public List<(string Type, object Payload)> Requests { get; } = [];

        public Exception? Failure { get; set; }

        public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, CancellationToken cancellationToken = default)
        {
            Requests.Add((type, payload!));
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult((TResponse)(object)new PrefsSetResult(7));
        }
    }

    private sealed class MemoryStorage : IPreferenceStorage
    {
        public PreferenceSnapshot Load() => PreferenceSnapshot.Empty;

        public void Save(PreferenceSnapshot snapshot)
        {
        }
    }

    private static readonly PreferenceDefinition[] _defs =
    [
        new("a.page", PreferenceKind.WholeNumber, "20", Min: 5, Max: 100),
        new("a.dense", PreferenceKind.Bool, "false"),
    ];

    [Fact]
    public async Task A_push_sends_prefs_set_with_the_key_and_value()
    {
        var wire = new Wire();

        await new WirePreferenceSync(wire).PushAsync("a.page", "30", TestContext.Current.CancellationToken);

        var (type, payload) = Assert.Single(wire.Requests);
        Assert.Equal("prefs.set", type);
        Assert.Equal(new PrefsSet("a.page", "30"), payload);
    }

    [Theory]
    [InlineData(WireErrorCodes.BadRequest)]
    [InlineData(WireErrorCodes.NotFound)]
    public async Task A_permanent_refusal_becomes_a_rejected_exception(string code)
    {
        var wire = new Wire { Failure = new WireRequestException(new WireError(code, "no", null)) };

        await Assert.ThrowsAsync<PreferenceRejectedException>(() =>
            new WirePreferenceSync(wire).PushAsync("a.page", "30", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_transient_error_is_not_turned_into_a_refusal()
    {
        var wire = new Wire { Failure = new WireRequestException(new WireError(WireErrorCodes.RateLimited, null, 100)) };

        await Assert.ThrowsAsync<WireRequestException>(() =>
            new WirePreferenceSync(wire).PushAsync("a.page", "30", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_refused_change_is_dropped_back_to_the_default_and_the_queue_moves_on()
    {
        var wire = new Wire();
        var store = new PreferenceStore(_defs, new MemoryStorage(), new RefuseFirst(wire));
        var changed = new List<string>();
        store.Changed += changed.Add;
        store.TrySet("a.page", "30");
        store.TrySet("a.dense", "true");
        changed.Clear();

        int sent = await store.FlushAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sent);
        Assert.Equal(20, store.GetInt("a.page"));
        Assert.True(store.GetBool("a.dense"));
        Assert.Empty(store.Pending);
        Assert.Equal(["a.page"], changed);
    }

    private sealed class RefuseFirst(Wire inner) : IPreferenceSync
    {
        public Task PushAsync(string key, string value, CancellationToken cancellationToken) =>
            key == "a.page"
                ? Task.FromException(new PreferenceRejectedException("no"))
                : new WirePreferenceSync(inner).PushAsync(key, value, cancellationToken);
    }
}
