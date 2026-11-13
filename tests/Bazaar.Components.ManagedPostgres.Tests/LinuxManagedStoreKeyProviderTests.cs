using Bazaar.Components.ManagedPostgres.Secrets;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class LinuxManagedStoreKeyProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-key-provider", Guid.NewGuid().ToString("N"));

    public LinuxManagedStoreKeyProviderTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_systemd_credential_supplies_the_key_ahead_of_the_machine_id()
    {
        var credentials = Path.Combine(_root, "creds");
        Directory.CreateDirectory(credentials);
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(credentials, LinuxManagedStoreKeyProvider.CredentialName), key, TestContext.Current.CancellationToken);
        var machineId = Path.Combine(_root, "machine-id");
        await File.WriteAllTextAsync(machineId, "abc", TestContext.Current.CancellationToken);

        var provider = new LinuxManagedStoreKeyProvider(credentials, machineId);

        Assert.Equal(key, await provider.GetKeyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_credential_of_the_wrong_size_is_refused()
    {
        var credentials = Path.Combine(_root, "creds");
        Directory.CreateDirectory(credentials);
        await File.WriteAllBytesAsync(Path.Combine(credentials, LinuxManagedStoreKeyProvider.CredentialName), new byte[16], TestContext.Current.CancellationToken);

        var provider = new LinuxManagedStoreKeyProvider(credentials, Path.Combine(_root, "missing"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetKeyAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Without_a_credential_the_key_is_derived_from_the_machine_id_and_is_stable()
    {
        var machineId = Path.Combine(_root, "machine-id");
        await File.WriteAllTextAsync(machineId, "0123456789abcdef\n", TestContext.Current.CancellationToken);
        var provider = new LinuxManagedStoreKeyProvider(credentialsDirectory: null, machineId);

        var first = await provider.GetKeyAsync(TestContext.Current.CancellationToken);
        var second = await provider.GetKeyAsync(TestContext.Current.CancellationToken);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Different_machine_ids_derive_different_keys()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        await File.WriteAllTextAsync(a, "host-a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(b, "host-b", TestContext.Current.CancellationToken);

        var keyA = await new LinuxManagedStoreKeyProvider(null, a).GetKeyAsync(TestContext.Current.CancellationToken);
        var keyB = await new LinuxManagedStoreKeyProvider(null, b).GetKeyAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public async Task With_no_credential_and_no_machine_id_the_provider_refuses()
    {
        var provider = new LinuxManagedStoreKeyProvider(null, Path.Combine(_root, "missing"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetKeyAsync(TestContext.Current.CancellationToken));
    }
}
