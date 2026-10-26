using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Secrets;

public class BootstrapSecretResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-secrets-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _environment = new()
    {
        ["BAZAAR_SET"] = "value-from-env",
        ["BAZAAR_EMPTY"] = "",
    };

    public BootstrapSecretResolverTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private BootstrapSecretResolver CreateResolver() => new(name => _environment.GetValueOrDefault(name));

    [Fact]
    public void CanResolve_accepts_env_and_file_only()
    {
        var resolver = CreateResolver();

        Assert.True(resolver.CanResolve("env:BAZAAR_SET"));
        Assert.True(resolver.CanResolve("file:/run/secrets/kek"));
        Assert.False(resolver.CanResolve("dpapi:kek.bin"));
        Assert.False(resolver.CanResolve("ENV:BAZAAR_SET"));
    }

    [Fact]
    public async Task Env_reference_returns_the_variable_value()
    {
        var value = await CreateResolver().ResolveAsync("env:BAZAAR_SET", TestContext.Current.CancellationToken);

        Assert.Equal("value-from-env", value);
    }

    [Fact]
    public async Task Missing_env_variable_throws_and_names_the_reference()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("env:BAZAAR_MISSING", TestContext.Current.CancellationToken).AsTask());

        Assert.Equal("env:BAZAAR_MISSING", ex.Reference);
        Assert.Contains("env:BAZAAR_MISSING", ex.Message);
        Assert.Contains("is not set", ex.Message);
    }

    [Fact]
    public async Task Empty_env_variable_throws()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("env:BAZAAR_EMPTY", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("is empty", ex.Message);
    }

    [Fact]
    public async Task Env_reference_without_a_name_throws()
    {
        await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("env:", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task File_reference_returns_contents_without_trailing_newline()
    {
        var path = Path.Combine(_directory, "kek");
        await File.WriteAllTextAsync(path, "file-secret\r\n", TestContext.Current.CancellationToken);

        var value = await CreateResolver().ResolveAsync("file:" + path, TestContext.Current.CancellationToken);

        Assert.Equal("file-secret", value);
    }

    [Fact]
    public async Task Missing_file_throws_and_names_the_reference()
    {
        var reference = "file:" + Path.Combine(_directory, "absent");

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync(reference, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(reference, ex.Reference);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public async Task Empty_file_throws()
    {
        var path = Path.Combine(_directory, "empty");
        await File.WriteAllTextAsync(path, "\n", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("file:" + path, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("is empty", ex.Message);
    }

    [Fact]
    public async Task Unreadable_file_throws_with_the_read_failure_as_inner_exception()
    {
        // A directory cannot be read as a file, on Windows and Linux alike.
        var path = Path.Combine(_directory, "not-a-file");
        Directory.CreateDirectory(path);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("file:" + path, TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("could not be read", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task File_reference_without_a_path_throws()
    {
        await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("file:", TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Unknown_scheme_throws()
    {
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => CreateResolver().ResolveAsync("dpapi:kek.bin", TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("unsupported scheme", ex.Message);
    }
}
