using Bazaar.Server.Providers.Linux;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Providers.Linux;

public class SystemdCredsSecretResolverTests : IDisposable
{
    private const string _sentinel = "sentinel-value-must-not-leak";
    private const string _credentialsVariable = "CREDENTIALS_DIRECTORY";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-creds-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new(StringComparer.Ordinal);

    public SystemdCredsSecretResolverTests()
    {
        Directory.CreateDirectory(_directory);
        _environment[_credentialsVariable] = _directory;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SystemdCredsSecretResolver CreateResolver() => new(
        name => _environment.GetValueOrDefault(name),
        (path, ct) => File.ReadAllTextAsync(path, ct));

    [Fact]
    public void CanResolve_accepts_systemd_creds_references_only()
    {
        var resolver = CreateResolver();

        Assert.True(resolver.CanResolve("systemd-creds:db-password"));
        Assert.False(resolver.CanResolve("env:BAZAAR_DB"));
        Assert.False(resolver.CanResolve("file:/run/secrets/kek"));
        Assert.False(resolver.CanResolve("SYSTEMD-CREDS:db-password"));
    }

    [Fact]
    public async Task Reads_the_credential_from_the_credentials_directory()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "db-password"), "db-secret\n", TestContext.Current.CancellationToken);

        var value = await CreateResolver().ResolveAsync("systemd-creds:db-password", TestContext.Current.CancellationToken);

        Assert.Equal("db-secret", value);
    }

    [Fact]
    public async Task Trims_trailing_carriage_return_and_line_feed()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "kek"), "kek-value\r\n\r\n", TestContext.Current.CancellationToken);

        var value = await CreateResolver().ResolveAsync("systemd-creds:kek", TestContext.Current.CancellationToken);

        Assert.Equal("kek-value", value);
    }

    [Fact]
    public async Task Missing_variable_throws_and_says_the_service_must_load_the_credential()
    {
        _environment.Remove(_credentialsVariable);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("systemd-creds:db-password", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("systemd-creds:db-password", ex.Reference);
        Assert.Contains("CREDENTIALS_DIRECTORY variable is not set", ex.Message);
        Assert.Contains("the service must load the credential with systemd", ex.Message);
    }

    [Fact]
    public async Task Empty_variable_is_treated_as_not_set()
    {
        _environment[_credentialsVariable] = string.Empty;

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("systemd-creds:db-password", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("CREDENTIALS_DIRECTORY variable is not set", ex.Message);
    }

    [Theory]
    [InlineData("systemd-creds:")]
    [InlineData("systemd-creds:.")]
    [InlineData("systemd-creds:..")]
    [InlineData("systemd-creds:../etc/shadow")]
    [InlineData("systemd-creds:sub/name")]
    [InlineData("systemd-creds:/etc/passwd")]
    [InlineData("systemd-creds:sub\\name")]
    public async Task Names_that_are_empty_or_reach_outside_the_directory_are_rejected(string reference)
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(reference, ex.Reference);
        Assert.Contains(reference, ex.Message);
    }

    [Fact]
    public async Task Missing_credential_file_throws_and_names_the_reference()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("systemd-creds:not-loaded", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("systemd-creds:not-loaded", ex.Message);
        Assert.Contains("not loaded for this service", ex.Message);
    }

    [Fact]
    public async Task Empty_credential_file_is_rejected()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "blank"), " \r\n", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("systemd-creds:blank", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("is empty", ex.Message);
    }

    [Fact]
    public async Task Error_messages_never_contain_a_secret_value()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "other"), _sentinel, TestContext.Current.CancellationToken);
        var resolver = CreateResolver();

        var failures = new[]
        {
            "systemd-creds:",
            "systemd-creds:../other",
            "systemd-creds:not-loaded",
        };
        foreach (var reference in failures)
        {
            var ex = await Assert.ThrowsAsync<SecretResolutionException>(
                () => resolver.ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());
            Assert.DoesNotContain(_sentinel, ex.Message);
        }

        _environment.Remove(_credentialsVariable);
        var unset = await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync("systemd-creds:other", TestContext.Current.CancellationToken).AsTask());
        Assert.DoesNotContain(_sentinel, unset.Message);
    }

    [Fact]
    public async Task Unreadable_credential_is_reported_without_the_value()
    {
        var resolver = new SystemdCredsSecretResolver(
            name => _environment.GetValueOrDefault(name),
            (_, _) => throw new UnauthorizedAccessException("denied"));

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => resolver.ResolveAsync("systemd-creds:locked", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("could not be read", ex.Message);
        Assert.NotNull(ex.InnerException);
    }
}
