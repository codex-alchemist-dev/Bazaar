using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Providers.Windows;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Providers.Windows;

[SupportedOSPlatform("windows")]
public class DpapiSecretResolverTests : IDisposable
{
    private const string _marker = "SECRET-MARKER-7f3a";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-dpapi-" + Guid.NewGuid().ToString("N"));

    public DpapiSecretResolverTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void CanResolve_accepts_dpapi_only()
    {
        var resolver = new DpapiSecretResolver();

        Assert.True(resolver.CanResolve("dpapi:kek.bin"));
        Assert.False(resolver.CanResolve("env:BAZAAR_DB"));
        Assert.False(resolver.CanResolve("file:/run/secrets/kek"));
        Assert.False(resolver.CanResolve("DPAPI:kek.bin"));
    }

    [Fact]
    public async Task Unsupported_scheme_throws_and_names_the_reference()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => new DpapiSecretResolver().ResolveAsync("env:BAZAAR_DB", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("env:BAZAAR_DB", ex.Reference);
    }

    [Fact]
    public async Task Empty_path_throws_and_names_the_reference()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => new DpapiSecretResolver().ResolveAsync("dpapi:", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("dpapi:", ex.Reference);
    }

    [Fact]
    public async Task Off_windows_resolution_throws_requires_windows_and_never_reads_the_file()
    {
        var path = Path.Combine(_directory, "kek.bin");
        await File.WriteAllTextAsync(path, _marker, TestContext.Current.CancellationToken);
        var reference = "dpapi:" + path;

        if (OperatingSystem.IsWindows())
        {
            // On Windows the same reference reads the file; the file holds plain text, so it fails as bad base64.
            var ex = await Assert.ThrowsAsync<SecretResolutionException>(
                () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

            Assert.Contains("does not hold base64", ex.Message);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SecretResolutionException>(
                () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(reference, ex.Reference);
            Assert.Contains("DPAPI requires Windows.", ex.Message);
        }
    }

    [Fact]
    public async Task Failure_messages_never_contain_the_secret_value()
    {
        // The file holds the marker as plain text: not valid base64, so the resolver must fail without echoing it.
        var path = Path.Combine(_directory, "kek.bin");
        await File.WriteAllTextAsync(path, _marker + "!", TestContext.Current.CancellationToken);
        var reference = "dpapi:" + path;

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains(reference, ex.Message);
        Assert.DoesNotContain(_marker, ex.Message);
    }

    [Fact]
    public async Task Missing_file_on_windows_names_the_reference_and_on_other_systems_still_refuses()
    {
        var reference = "dpapi:" + Path.Combine(_directory, "missing.bin");

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(reference, ex.Reference);
        Assert.Contains(reference, ex.Message);
        Assert.Contains(OperatingSystem.IsWindows() ? "does not exist" : "requires Windows", ex.Message);
    }

    [Fact]
    public async Task Protected_value_round_trips_on_windows_and_trims_trailing_line_endings()
    {
        var reference = "dpapi:" + Path.Combine(_directory, "kek.bin");
        var path = reference["dpapi:".Length..];
        var protectedBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(Encoding.UTF8.GetBytes("round-trip-value\r\n"), optionalEntropy: null, DataProtectionScope.LocalMachine)
            : Encoding.UTF8.GetBytes("unused");
        await File.WriteAllTextAsync(path, Convert.ToBase64String(protectedBytes) + "\n", TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            var value = await new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken);

            Assert.Equal("round-trip-value", value);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<SecretResolutionException>(
                () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

            Assert.Contains("requires Windows", ex.Message);
        }
    }

    [Fact]
    public async Task Protected_bytes_that_fail_to_unprotect_throw_without_the_value()
    {
        var reference = "dpapi:" + Path.Combine(_directory, "kek.bin");
        var path = reference["dpapi:".Length..];
        if (OperatingSystem.IsWindows())
        {
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(_marker), optionalEntropy: null, DataProtectionScope.LocalMachine);
            protectedBytes[^1] ^= 0xFF;
            await File.WriteAllTextAsync(path, Convert.ToBase64String(protectedBytes), TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<SecretResolutionException>(
                () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

            Assert.Contains("could not unprotect", ex.Message);
            Assert.DoesNotContain(_marker, ex.Message);
        }
        else
        {
            await File.WriteAllTextAsync(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(_marker)), TestContext.Current.CancellationToken);

            var ex = await Assert.ThrowsAsync<SecretResolutionException>(
                () => new DpapiSecretResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

            Assert.Contains("requires Windows", ex.Message);
            Assert.DoesNotContain(_marker, ex.Message);
        }
    }
}
