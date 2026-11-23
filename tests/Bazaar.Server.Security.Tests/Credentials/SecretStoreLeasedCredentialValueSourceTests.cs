using System.Text;
using Bazaar.Secrets;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Credentials;

namespace Bazaar.Server.Security.Tests.Credentials;

public class SecretStoreLeasedCredentialValueSourceTests
{
    private static readonly CredentialId _credential = new(Guid.Parse("11111111-2222-3333-4444-555555555555"));

    [Fact]
    public async Task Reads_the_agent_scope_under_the_documented_name_caller_and_purpose()
    {
        var store = new FakeStore { Value = "ghs_Example0Token1Value" };
        var source = new SecretStoreLeasedCredentialValueSource(store);

        var bytes = await source.ResolveAsync("agent-a", _credential, CancellationToken.None);

        Assert.Equal("ghs_Example0Token1Value", Encoding.UTF8.GetString(bytes!));
        Assert.Equal("credential.11111111222233334444555555555555", store.Name!.Value);
        Assert.Equal("agent:agent-a", store.Scope!.Key);
        Assert.Equal("lease-matcher", ((HostCaller)store.Caller!).Component);
        Assert.Equal("lease.match", store.Purpose!.Value);
    }

    [Fact]
    public async Task Missing_secret_resolves_to_null()
    {
        var source = new SecretStoreLeasedCredentialValueSource(new FakeStore { Value = null });

        Assert.Null(await source.ResolveAsync("agent-a", _credential, CancellationToken.None));
    }

    [Fact]
    public async Task Agent_id_the_store_cannot_hold_resolves_to_null_without_a_read()
    {
        var store = new FakeStore { Value = "x" };
        var source = new SecretStoreLeasedCredentialValueSource(store);

        Assert.Null(await source.ResolveAsync("Not A Valid Agent", _credential, CancellationToken.None));
        Assert.Null(store.Name);
    }

    [Fact]
    public async Task No_secret_store_resolves_to_null()
    {
        var source = new SecretStoreLeasedCredentialValueSource();

        Assert.Null(await source.ResolveAsync("agent-a", _credential, CancellationToken.None));
    }

    private sealed class FakeStore : ISecretStore
    {
        public string? Value { get; set; }

        public SecretCaller? Caller { get; private set; }

        public SecretScope? Scope { get; private set; }

        public SecretName? Name { get; private set; }

        public SecretPurpose? Purpose { get; private set; }

        public Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct)
        {
            Caller = caller;
            Scope = scope;
            Name = name;
            Purpose = purpose;
            return Value is null
                ? throw new SecretNotFoundException("missing")
                : Task.FromResult(SecretValue.FromText(Value));
        }

        public Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct) => throw new NotSupportedException();

        public Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct) => throw new NotSupportedException();
    }
}
