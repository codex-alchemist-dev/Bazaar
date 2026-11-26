using System.Net;
using System.Security.Cryptography;
using Bazaar.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Identity.Kek.OpenBao.Tests;

/// <summary>The OpenBao transit KEK against a fake transit engine: round trip, binding, key setup, failures, and registration.</summary>
public sealed class OpenBaoKekTests
{
    private static OpenBaoOptions Options(string address = "https://bao.example.invalid:8200", string? ns = null) =>
        new(new Uri(address), "transit", "bazaar-kek", "env:BAO_TOKEN", ns, TimeSpan.FromSeconds(5));

    private static OpenBaoKekProvider NewProvider(FakeTransitHandler handler, string token = "s.test-token", string? ns = null) =>
        new(Options(ns: ns), () => token, handler);

    private static Dictionary<string, string?> Settings(params (string Key, string? Value)[] extra)
    {
        var map = new Dictionary<string, string?>
        {
            [OpenBaoOptions.AddressKey] = "https://bao.example.invalid:8200",
            [OpenBaoOptions.TokenRefKey] = "env:BAO_TOKEN",
        };
        foreach (var (key, value) in extra)
            map[key] = value;
        return map;
    }

    [Fact]
    public async Task Wrap_then_unwrap_round_trips_and_the_wrap_is_a_transit_ciphertext()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewProvider(new FakeTransitHandler { KeyExists = true });
        var dek = RandomNumberGenerator.GetBytes(32);
        var aad = Guid.CreateVersion7().ToByteArray();

        var wrapped = await kek.WrapAsync(dek, aad, ct);

        Assert.StartsWith("vault:v1:", System.Text.Encoding.UTF8.GetString(wrapped), StringComparison.Ordinal);
        Assert.Equal(dek, await kek.UnwrapAsync(wrapped, aad, ct));
        Assert.Equal("openbao-transit-v1:transit/bazaar-kek", kek.KekId);
    }

    [Fact]
    public async Task A_wrap_is_bound_to_its_associated_data()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewProvider(new FakeTransitHandler { KeyExists = true });
        var wrapped = await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), Guid.CreateVersion7().ToByteArray(), ct);

        await Assert.ThrowsAsync<KekUnwrapException>(async () => await kek.UnwrapAsync(wrapped, Guid.CreateVersion7().ToByteArray(), ct));
    }

    [Fact]
    public async Task Bytes_from_another_provider_are_an_unwrap_failure_without_a_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeTransitHandler { KeyExists = true };
        using var kek = NewProvider(handler);

        await Assert.ThrowsAsync<KekUnwrapException>(async () => await kek.UnwrapAsync(RandomNumberGenerator.GetBytes(60), new byte[] { 1, 2, 3 }, ct));
        await Assert.ThrowsAsync<KekUnwrapException>(async () => await kek.UnwrapAsync("not-vault".AsMemory().ToArray().Select(c => (byte)c).ToArray(), new byte[] { 1 }, ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_missing_key_is_created_derived_on_the_first_wrap_and_only_once()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeTransitHandler();
        using var kek = NewProvider(handler);

        await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), new byte[] { 1 }, ct);
        await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), new byte[] { 2 }, ct);

        Assert.True(handler.KeyExists);
        Assert.True(handler.KeyIsDerived);
        Assert.Equal(1, handler.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/keys/bazaar-kek", StringComparison.Ordinal)));
        Assert.Equal(1, handler.Requests.Count(r => r.Method == "GET"));
    }

    [Fact]
    public async Task An_existing_key_that_is_not_derived_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = NewProvider(new FakeTransitHandler { KeyExists = true, KeyIsDerived = false });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), new byte[] { 1 }, ct));
        Assert.Contains("derived", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_error_or_bad_token_is_not_read_as_an_unwrap_failure_and_never_leaks_the_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeTransitHandler { KeyExists = true };
        using var good = NewProvider(handler);
        var wrapped = await good.WrapAsync(RandomNumberGenerator.GetBytes(32), new byte[] { 9 }, ct);

        using var badToken = NewProvider(handler, token: "s.wrong-secret-token");
        var denied = await Assert.ThrowsAsync<HttpRequestException>(async () => await badToken.UnwrapAsync(wrapped, new byte[] { 9 }, ct));
        Assert.DoesNotContain("s.wrong-secret-token", denied.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        handler.FailWith = HttpStatusCode.InternalServerError;
        var down = await Assert.ThrowsAsync<HttpRequestException>(async () => await good.UnwrapAsync(wrapped, new byte[] { 9 }, ct));
        Assert.Equal(HttpStatusCode.InternalServerError, down.StatusCode);
    }

    [Fact]
    public async Task The_namespace_header_is_sent_when_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new FakeTransitHandler { KeyExists = true };
        using var kek = NewProvider(handler, ns: "team-a");

        await kek.WrapAsync(RandomNumberGenerator.GetBytes(32), new byte[] { 1 }, ct);

        Assert.All(handler.Requests, r => Assert.Equal("team-a", r.Namespace));
    }

    [Fact]
    public void Options_apply_defaults_and_validate_every_setting()
    {
        var options = OpenBaoOptions.From(key => Settings().GetValueOrDefault(key));
        Assert.Equal("transit", options.Mount);
        Assert.Equal("bazaar-kek", options.KeyName);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Timeout);
        Assert.Null(options.Namespace);

        string? Get(string key, params (string, string?)[] extra) => Settings(extra).GetValueOrDefault(key);
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.AddressKey, null))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.AddressKey, "http://bao.example.invalid:8200"))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.AddressKey, "not a url"))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.TokenRefKey, "s.literal-token"))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.TokenRefKey, null))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.KeyNameKey, "a/b"))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.MountKey, "../sys"))));
        Assert.Throws<ArgumentException>(() => OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.TimeoutSecondsKey, "0"))));
        Assert.Equal("transit/team", OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.MountKey, "/transit/team/"))).Mount);
        Assert.Equal(new Uri("http://127.0.0.1:8200"), OpenBaoOptions.From(k => Get(k, (OpenBaoOptions.AddressKey, "http://127.0.0.1:8200"))).Address);
    }

    [Fact]
    public void The_token_reference_reads_env_and_absolute_files_on_every_call()
    {
        var name = "BAZAAR_TEST_BAO_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "  s.from-env \n");
        try
        {
            Assert.Equal("s.from-env", TokenReference.Read("env:" + name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        Assert.Throws<InvalidOperationException>(() => TokenReference.Read("env:" + name));
        Assert.Throws<InvalidOperationException>(() => TokenReference.Read("file:relative/token"));
        Assert.Throws<InvalidOperationException>(() => TokenReference.Read("literal"));

        var path = Path.Combine(Path.GetTempPath(), "bao-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "s.first\n");
        try
        {
            Assert.Equal("s.first", TokenReference.Read("file:" + path));
            File.WriteAllText(path, "s.rotated\n");
            Assert.Equal("s.rotated", TokenReference.Read("file:" + path));
            File.WriteAllText(path, "  \n");
            Assert.Throws<InvalidOperationException>(() => TokenReference.Read("file:" + path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_kek_component_picks_openbao_when_its_module_is_loaded_and_refuses_it_when_not()
    {
        var root = Path.Combine(Path.GetTempPath(), "bazaar-bao-" + Guid.CreateVersion7().ToString("N"));
        try
        {
            var settings = Settings((KekComponent.KekProviderKey, "openbao"), (KekComponent.DataRootKey, root));
            var config = new FakeConfiguration(settings);

            var loaded = new ServiceCollection();
            loaded.AddBazaarComponents(config, [new KekComponent(), new OpenBaoKekComponent()]);
            using (var provider = loaded.BuildServiceProvider())
            {
                var kek = provider.GetRequiredService<IKekProvider>();
                Assert.IsType<OpenBaoKekProvider>(kek);
                (kek as IDisposable)?.Dispose();
            }

            var missing = new ServiceCollection();
            missing.AddBazaarComponents(config, [new KekComponent()]);
            using var bare = missing.BuildServiceProvider();
            Assert.Throws<NotSupportedException>(() => bare.GetRequiredService<IKekProvider>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_rewrap_from_a_file_kek_to_openbao_moves_the_wraps_and_a_failed_request_aborts_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "bazaar-bao-" + Guid.CreateVersion7().ToString("N"));
        try
        {
            using var source = FileKekProvider.Open(Path.Combine(root, "keys", "kek.bin"));
            var handler = new FakeTransitHandler { KeyExists = true };
            using var target = NewProvider(handler);
            var store = new ListStore();
            for (var i = 0; i < 3; i++)
            {
                var id = Guid.CreateVersion7();
                store.Rows.Add(new KekWrapRecord(id, await source.WrapAsync(RandomNumberGenerator.GetBytes(32), id.ToByteArray(), ct), id.ToByteArray()));
            }

            handler.FailWith = HttpStatusCode.ServiceUnavailable;
            await Assert.ThrowsAsync<HttpRequestException>(async () => await KekRewriter.RunAsync(source, target, store, new NullAudit(), ct: ct));
            Assert.All(store.Rows, r => Assert.False(r.Wrapped.AsSpan().StartsWith("vault:"u8)));

            handler.FailWith = null;
            var report = await KekRewriter.RunAsync(source, target, store, new NullAudit(), batchSize: 2, ct: ct);
            Assert.True(report.Succeeded);
            Assert.Equal(3, report.Rewrapped);
            foreach (var row in store.Rows)
                Assert.Equal(32, (await target.UnwrapAsync(row.Wrapped, row.AssociatedData, ct)).Length);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ListStore : IKekRewrapStore
    {
        public readonly List<KekWrapRecord> Rows = [];

        public Task<IReadOnlyList<KekWrapRecord>> ListLiveWrapsAsync(Guid? afterId, int limit, CancellationToken ct)
        {
            IReadOnlyList<KekWrapRecord> page = Rows.Where(r => afterId is null || r.Id.CompareTo(afterId.Value) > 0).OrderBy(r => r.Id).Take(limit).ToList();
            return Task.FromResult(page);
        }

        public Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, string kekId, CancellationToken ct)
        {
            var index = Rows.FindIndex(r => r.Id == id);
            if (index < 0 || !Rows[index].Wrapped.AsSpan().SequenceEqual(expected.Span))
                return Task.FromResult(false);
            Rows[index] = Rows[index] with { Wrapped = replacement.ToArray() };
            return Task.FromResult(true);
        }
    }

    private sealed class NullAudit : IKekAuditSink
    {
        public Task RecordAsync(string kind, string payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}
