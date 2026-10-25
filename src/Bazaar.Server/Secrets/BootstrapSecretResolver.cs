namespace Bazaar.Server.Secrets;

/// <summary>Resolves <c>env:NAME</c> and <c>file:PATH</c> references. <c>dpapi:</c> and <c>systemd-creds:</c> come from the OS providers.</summary>
public sealed class BootstrapSecretResolver : ISecretResolver
{
    private const string _environmentPrefix = "env:";
    private const string _filePrefix = "file:";

    private readonly Func<string, string?> _readEnvironmentVariable;

    public BootstrapSecretResolver()
        : this(name => Environment.GetEnvironmentVariable(name))
    {
    }

    public BootstrapSecretResolver(Func<string, string?> readEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        _readEnvironmentVariable = readEnvironmentVariable;
    }

    public bool CanResolve(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.StartsWith(_environmentPrefix, StringComparison.Ordinal)
            || reference.StartsWith(_filePrefix, StringComparison.Ordinal);
    }

    public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.StartsWith(_environmentPrefix, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(ResolveEnvironmentVariable(reference));
        }

        if (reference.StartsWith(_filePrefix, StringComparison.Ordinal))
        {
            return ResolveFileAsync(reference, cancellationToken);
        }

        throw new SecretResolutionException(reference, "unsupported scheme; bootstrap resolves env: and file: only.");
    }

    private string ResolveEnvironmentVariable(string reference)
    {
        var name = reference[_environmentPrefix.Length..];
        if (name.Length == 0)
        {
            throw new SecretResolutionException(reference, "the variable name is empty.");
        }

        var value = _readEnvironmentVariable(name);
        if (value is null)
        {
            throw new SecretResolutionException(reference, $"environment variable '{name}' is not set.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SecretResolutionException(reference, $"environment variable '{name}' is empty.");
        }

        return value;
    }

    private static async ValueTask<string> ResolveFileAsync(string reference, CancellationToken cancellationToken)
    {
        var path = reference[_filePrefix.Length..];
        if (path.Length == 0)
        {
            throw new SecretResolutionException(reference, "the file path is empty.");
        }

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            throw new SecretResolutionException(reference, "the file does not exist.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretResolutionException(reference, "the file could not be read.", ex);
        }

        // Trailing line endings are an artifact of how files are written, not part of the secret.
        var value = text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SecretResolutionException(reference, "the file is empty.");
        }

        return value;
    }
}
