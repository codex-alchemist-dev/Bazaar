using Bazaar.Server.Secrets;

namespace Bazaar.Server.Providers.Linux;

/// <summary>
/// Resolves <c>systemd-creds:&lt;name&gt;</c> references (doc 03). systemd decrypts credentials that a unit loads with
/// <c>LoadCredential</c> or <c>LoadCredentialEncrypted</c> into the directory named by <c>CREDENTIALS_DIRECTORY</c>.
/// Failures name the reference, never the value.
/// </summary>
public sealed class SystemdCredsSecretResolver : ISecretResolver
{
    private const string _prefix = "systemd-creds:";
    private const string _credentialsDirectoryVariable = "CREDENTIALS_DIRECTORY";
    private const string _currentDirectory = ".";
    private const string _parentDirectory = "..";
    private static readonly char[] _forbiddenNameCharacters = ['/', '\\', '\0'];

    private readonly Func<string, string?> _readEnvironmentVariable;
    private readonly Func<string, CancellationToken, Task<string>> _readFileAsync;

    public SystemdCredsSecretResolver()
        : this(name => Environment.GetEnvironmentVariable(name), (path, ct) => File.ReadAllTextAsync(path, ct))
    {
    }

    public SystemdCredsSecretResolver(
        Func<string, string?> readEnvironmentVariable,
        Func<string, CancellationToken, Task<string>> readFileAsync)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(readFileAsync);
        _readEnvironmentVariable = readEnvironmentVariable;
        _readFileAsync = readFileAsync;
    }

    public bool CanResolve(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith(_prefix, StringComparison.Ordinal);
    }

    public async ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!CanResolve(reference))
        {
            throw new SecretResolutionException(reference, "unsupported scheme; systemd-creds resolves systemd-creds: only.");
        }

        var name = reference[_prefix.Length..];
        if (name.Length == 0)
        {
            throw new SecretResolutionException(reference, "the credential name is empty.");
        }

        if (!IsPlainName(name))
        {
            throw new SecretResolutionException(reference, "the credential name must be a single file name without path separators.");
        }

        var directory = _readEnvironmentVariable(_credentialsDirectoryVariable);
        if (string.IsNullOrEmpty(directory))
        {
            throw new SecretResolutionException(
                reference,
                "the CREDENTIALS_DIRECTORY variable is not set; the service must load the credential with systemd.");
        }

        var path = Path.Combine(directory, name);
        string text;
        try
        {
            text = await _readFileAsync(path, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw new SecretResolutionException(reference, "the credential is not loaded for this service.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SecretResolutionException(reference, "the credential is not loaded for this service.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretResolutionException(reference, "the credential could not be read.", ex);
        }

        // Trailing line endings are an artifact of how the credential was written, not part of the secret.
        var value = text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SecretResolutionException(reference, "the credential is empty.");
        }

        return value;
    }

    /// <summary>A credential name must be one file name: no separators, no NUL, and not the current or parent directory.</summary>
    private static bool IsPlainName(string name)
    {
        if (name == _currentDirectory || name == _parentDirectory)
        {
            return false;
        }

        return name.IndexOfAny(_forbiddenNameCharacters) < 0;
    }
}
