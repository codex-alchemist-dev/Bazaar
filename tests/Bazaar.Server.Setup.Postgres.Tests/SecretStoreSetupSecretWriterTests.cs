using System.Text;
using Bazaar.Secrets;
using Bazaar.Server.Setup.Postgres;

namespace Bazaar.Server.Setup.Postgres.Tests;

public sealed class SecretStoreSetupSecretWriterTests
{
    private sealed class RecordingStore : ISecretStore
    {
        public SecretCaller? Caller { get; private set; }
        public SecretScope? Scope { get; private set; }
        public SecretName? Name { get; private set; }
        public string? Value { get; private set; }
        public bool Throw { get; init; }

        public Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct)
        {
            Caller = caller;
            Scope = scope;
            Name = name;
            Value = Encoding.UTF8.GetString(value.Bytes);
            return Throw ? throw new InvalidOperationException("store failed") : Task.CompletedTask;
        }

        public Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct) => throw new NotSupportedException();

        public Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_reference_is_stored_in_the_installation_scope_under_a_dotted_name()
    {
        var store = new RecordingStore();
        var writer = new SecretStoreSetupSecretWriter(store);

        await writer.WriteAsync("secret://bazaar/providers/anthropic/key", "the-value", TestContext.Current.CancellationToken);

        Assert.True(writer.StoreAvailable);
        Assert.Equal(SecretScope.Installation, store.Scope);
        Assert.Equal("providers.anthropic.key", store.Name!.Value);
        Assert.Equal("the-value", store.Value);
        Assert.Equal(new HostCaller(SecretStoreSetupSecretWriter.CallerComponent), store.Caller);
    }

    [Theory]
    [InlineData("env:SOMETHING")]
    [InlineData("secret://bazaar/")]
    [InlineData("secret://bazaar/Providers/key")]
    [InlineData("secret://bazaar/a.b/key")]
    [InlineData("secret://bazaar/a//key")]
    [InlineData("secret://bazaar/-a/key")]
    public async Task A_reference_outside_the_name_rule_is_refused_without_echoing_it(string reference)
    {
        var store = new RecordingStore();
        var writer = new SecretStoreSetupSecretWriter(store);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => writer.WriteAsync(reference, "v", TestContext.Current.CancellationToken));

        Assert.DoesNotContain(reference, error.Message, StringComparison.Ordinal);
        Assert.Null(store.Name);
    }

    [Fact]
    public async Task A_store_failure_does_not_put_the_value_in_the_exception()
    {
        var writer = new SecretStoreSetupSecretWriter(new RecordingStore { Throw = true });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteAsync("secret://bazaar/a/b", "canary-value-123", TestContext.Current.CancellationToken));

        Assert.DoesNotContain("canary-value-123", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_distinct_references_never_share_a_name()
    {
        // Dots and capitals are refused in a segment, so the dotted join cannot collide.
        Assert.NotEqual(SecretStoreSetupSecretWriter.NameFor("secret://bazaar/a/b-c").Value, SecretStoreSetupSecretWriter.NameFor("secret://bazaar/a-b/c").Value);
    }
}
