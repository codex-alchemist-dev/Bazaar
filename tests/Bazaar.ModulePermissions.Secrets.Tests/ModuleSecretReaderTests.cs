using System.Text;
using Bazaar.Secrets;

namespace Bazaar.ModulePermissions.Secrets.Tests;

public class ModuleSecretReaderTests
{
    private const string _module = "weather-mod";

    // Short, obviously fake, and built from parts so no secret-shaped literal sits in the repository.
    private static readonly string _fake = "not" + "-a-" + "secret";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ListSink : IModulePermissionDenialSink
    {
        public List<ModulePermissionDenial> Denials { get; } = [];

        public void Record(ModulePermissionDenial denial) => Denials.Add(denial);
    }

    private sealed class FakeStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public List<(SecretCaller Caller, SecretScope Scope, string Name, string Purpose)> Reads { get; } = [];

        public Exception? Fail { get; set; }

        public Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Reads.Add((caller, scope, name.Value, purpose.Value));
            if (Fail is not null)
            {
                throw Fail;
            }

            return Values.TryGetValue(name.Value, out var text)
                ? Task.FromResult(SecretValue.FromText(text))
                : throw new SecretNotFoundException($"No secret {name.Value}.");
        }

        public Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private static (ModulePermissionGate Gate, ListSink Sink, FakeStore Store) Setup(params string[] permissions)
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        gate.Activate(_module, permissions);
        return (gate, sink, new FakeStore());
    }

    private static string Text(SecretValue value) => Encoding.UTF8.GetString(value.Bytes);

    [Fact]
    public async Task An_exact_grant_reads_the_secret_from_the_module_namespace()
    {
        var (gate, sink, store) = Setup("secret:weather/api-key");
        store.Values["mod_weather-mod_weather.api-key"] = _fake;

        using var value = await new ModuleSecretReader(gate, store, _module).ReadAsync("weather/api-key", Ct);

        Assert.Equal(_fake, Text(value));
        Assert.Empty(sink.Denials);
        var read = Assert.Single(store.Reads);
        Assert.Equal(new HostCaller("module." + _module), read.Caller);
        Assert.Equal(SecretScope.Installation, read.Scope);
        Assert.Equal("module.secret.read", read.Purpose);
    }

    [Fact]
    public async Task A_prefix_grant_covers_names_below_it()
    {
        var (gate, _, store) = Setup("secret:weather/*");
        store.Values["mod_weather-mod_weather.api-key"] = _fake;
        store.Values["mod_weather-mod_weather.deep.key"] = _fake;
        var reader = new ModuleSecretReader(gate, store, _module);

        using var one = await reader.ReadAsync("weather/api-key", Ct);
        using var two = await reader.ReadAsync("weather/deep/key", Ct);

        Assert.Equal(_fake, Text(one));
        Assert.Equal(_fake, Text(two));
    }

    [Theory]
    [InlineData("weather")]
    [InlineData("weather-admin/key")]
    [InlineData("weatherx/key")]
    [InlineData("other/api-key")]
    public async Task A_prefix_grant_does_not_cover_the_prefix_itself_or_a_sibling(string name)
    {
        var (gate, sink, store) = Setup("secret:weather/*");

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync(name, Ct));

        Assert.Empty(store.Reads);
        Assert.NotEmpty(sink.Denials);
    }

    [Fact]
    public async Task A_name_outside_the_grants_never_reaches_the_store_whether_or_not_it_exists()
    {
        var (gate, _, store) = Setup("secret:weather/api-key");
        store.Values["mod_weather-mod_billing.key"] = _fake;
        var reader = new ModuleSecretReader(gate, store, _module);

        var present = await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => reader.ReadAsync("billing/key", Ct));
        var absent = await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => reader.ReadAsync("billing/none", Ct));

        Assert.Equal(present.Decision, absent.Decision);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task A_grant_of_another_class_does_not_allow_a_secret_read()
    {
        var (gate, _, store) = Setup("data.read:weather/api-key", "network:api.example.com");

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync("weather/api-key", Ct));

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task A_module_that_is_not_running_is_refused_before_the_store()
    {
        var (gate, _, store) = Setup("secret:weather/*");
        gate.Deactivate(_module);

        var e = await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync("weather/api-key", Ct));

        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, e.Decision);
        Assert.Empty(store.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("weather/../api-key")]
    [InlineData("weather/./key")]
    [InlineData("weather//key")]
    [InlineData("weather\\key")]
    [InlineData("weather/*")]
    [InlineData("weather/ke\ty")]
    [InlineData("weather/ke\u0000y")]
    [InlineData("weather/ke‮y")]
    public async Task A_malformed_name_is_refused_before_the_store_even_under_a_broad_grant(string name)
    {
        var (gate, sink, store) = Setup("secret:weather/*", "secret:weather/api-key");

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync(name, Ct));

        Assert.Empty(store.Reads);
        Assert.All(sink.Denials, d => Assert.Equal(ModuleAccessDecision.DeniedInvalidTarget, d.Decision));
        Assert.NotEmpty(sink.Denials);
    }

    [Fact]
    public async Task An_over_long_name_is_refused_before_the_store()
    {
        var name = "weather/" + new string('a', ModuleSecretReader.MaxNameLength);
        var (gate, _, store) = Setup("secret:weather/*");

        await Assert.ThrowsAsync<ModuleSecretRefusedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync(name, Ct));

        Assert.Empty(store.Reads);
    }

    [Theory]
    [InlineData("/weather/key")]
    [InlineData("weather/key/")]
    [InlineData("Weather/key")]
    [InlineData("weather/api_key")]
    [InlineData("weather/api.key")]
    [InlineData("weather/-key")]
    public async Task A_granted_name_that_cannot_map_into_the_namespace_is_refused_before_the_store(string name)
    {
        // The grants here are the only way to get past the gate with such a name; the reader still refuses it.
        var (gate, _, store) = Setup("secret:/weather/key", "secret:weather/key/", "secret:Weather/key", "secret:weather/api_key", "secret:weather/api.key", "secret:weather/-key");

        await Assert.ThrowsAsync<ModuleSecretRefusedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync(name, Ct));

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task One_module_cannot_read_the_secret_of_another_even_with_the_same_grant_text()
    {
        var sink = new ListSink();
        var gate = new ModulePermissionGate(sink, TimeProvider.System);
        gate.Activate("mod-a", ["secret:shared/key"]);
        gate.Activate("mod-b", ["secret:shared/key"]);
        var store = new FakeStore();
        store.Values["mod_mod-a_shared.key"] = _fake;
        store.Values["mod_mod-b_shared.key"] = _fake + "-b";

        using var a = await new ModuleSecretReader(gate, store, "mod-a").ReadAsync("shared/key", Ct);
        using var b = await new ModuleSecretReader(gate, store, "mod-b").ReadAsync("shared/key", Ct);

        Assert.Equal(_fake, Text(a));
        Assert.Equal(_fake + "-b", Text(b));
        Assert.Equal(["mod_mod-a_shared.key", "mod_mod-b_shared.key"], store.Reads.Select(r => r.Name));
    }

    [Fact]
    public async Task A_module_cannot_name_a_platform_secret_through_the_mapping()
    {
        var (gate, _, store) = Setup("secret:weather/*");
        store.Values["provider.key"] = _fake;
        var reader = new ModuleSecretReader(gate, store, _module);

        await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => reader.ReadAsync("provider.key", Ct));
        await Assert.ThrowsAsync<ModulePermissionDeniedException>(() => reader.ReadAsync("mod_weather-mod_weather.api-key", Ct));

        Assert.Empty(store.Reads);
    }

    [Fact]
    public void Two_names_never_map_to_the_same_store_name()
    {
        var one = ModuleSecretReader.Map("a-b", "c/d");
        var two = ModuleSecretReader.Map("a", "b-c/d");
        var three = ModuleSecretReader.Map("a-b", "c-d");
        var four = ModuleSecretReader.Map("a.b", "c/d");
        var five = ModuleSecretReader.Map("a", "b/c/d");
        var six = ModuleSecretReader.Map("a.b.c", "d");

        Assert.Equal(6, new[] { one.Value, two.Value, three.Value, four.Value, five.Value, six.Value }.Distinct().Count());
    }

    [Fact]
    public void A_dotted_module_id_maps_inside_its_own_prefix()
    {
        Assert.Equal("mod_acme.weather_api.key", ModuleSecretReader.Map("acme.weather", "api/key").Value);
    }

    [Fact]
    public void The_longest_id_and_name_still_fit_a_store_name()
    {
        var id = new string('a', ModuleSecretReader.MaxModuleIdLength);
        var name = new string('b', ModuleSecretReader.MaxNameLength);

        Assert.True(ModuleSecretReader.Map(id, name).Value.Length <= 128);
        Assert.Throws<ModuleSecretRefusedException>(() => ModuleSecretReader.Map(id + "a", name));
    }

    [Fact]
    public async Task A_cancelled_read_is_not_started()
    {
        var (gate, _, store) = Setup("secret:weather/*");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync("weather/api-key", cts.Token));

        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task A_cancellation_inside_the_store_is_not_hidden()
    {
        var (gate, _, store) = Setup("secret:weather/*");
        store.Fail = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync("weather/api-key", Ct));
    }

    [Fact]
    public async Task A_store_failure_surfaces_without_the_value_or_the_store_message()
    {
        var (gate, _, store) = Setup("secret:weather/*");
        store.Fail = new InvalidOperationException("store broke near " + _fake + " at mod_weather-mod_weather.api-key");

        var e = await Assert.ThrowsAsync<ModuleSecretRefusedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync("weather/api-key", Ct));

        Assert.DoesNotContain(_fake, e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("mod_", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task A_missing_granted_secret_is_refused_without_the_store_name()
    {
        var (gate, _, store) = Setup("secret:weather/*");

        var e = await Assert.ThrowsAsync<ModuleSecretRefusedException>(() => new ModuleSecretReader(gate, store, _module).ReadAsync("weather/gone", Ct));

        Assert.DoesNotContain("weather", e.Message, StringComparison.Ordinal);
        Assert.Null(e.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Weather")]
    [InlineData("a_b")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("a.-b")]
    [InlineData("a/b")]
    [InlineData("-a")]
    public void A_module_id_that_could_blur_the_mapping_is_refused(string moduleId)
    {
        var (gate, _, store) = Setup();

        Assert.ThrowsAny<ArgumentException>(() => new ModuleSecretReader(gate, store, moduleId));
    }

    [Fact]
    public void The_read_value_never_prints_itself()
    {
        using var value = SecretValue.FromText(_fake);

        Assert.DoesNotContain(_fake, value.ToString(), StringComparison.Ordinal);
    }
}
