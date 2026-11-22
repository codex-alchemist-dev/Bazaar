namespace Bazaar.Server.Git;

public enum GitOperation
{
    Read,
    Push,
}

/// <summary>
/// The token seam (doc 52). The Identity Service implements it in host composition (doc 14); tests and the dev host use a fake.
/// No forge adapter reads a token from an environment variable or a file.
/// </summary>
public interface IGitCredentialSource
{
    Task<GitCredential> LeaseAsync(RepoRef repo, GitOperation operation, CancellationToken cancellationToken);
}

/// <summary>
/// A short-lived credential, held in memory only. <see cref="ToString"/> never prints the secret. Dispose clears it.
/// </summary>
public sealed class GitCredential : IDisposable
{
    private char[]? _secret;

    public GitCredential(string username, string secret, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        Username = username;
        _secret = secret.ToCharArray();
        ExpiresAt = expiresAt;
    }

    public string Username { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The secret. Throws after <see cref="Dispose"/>.</summary>
    public string Reveal() => _secret is null ? throw new ObjectDisposedException(nameof(GitCredential)) : new string(_secret);

    public void Dispose()
    {
        if (_secret is { } s)
        {
            Array.Clear(s);
            _secret = null;
        }
    }

    public override string ToString() => $"GitCredential({Username}, expires {ExpiresAt:O})";
}
