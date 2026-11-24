using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Client.SignIn;

/// <summary>
/// Keeps the session in one file, sealed by the platform's <see cref="ISecretProtector"/> (doc 45 section 2.6, doc 32 section 5),
/// so a restart stays signed in and no token reaches the disk in the clear. A file that cannot be opened (another user, a
/// restored disk, damage) reads as "not signed in" and is removed, so the person is sent back to the sign-in page instead of
/// into an error.
/// </summary>
public sealed class ProtectedFileSessionStore : ISessionStore, IDisposable
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates a store that keeps the sealed session at <paramref name="path"/>.</summary>
    public ProtectedFileSessionStore(string path, ISecretProtector protector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(protector);
        _path = path;
        _protector = protector;
    }

    /// <inheritdoc />
    public async Task<ClientSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] sealedBytes;
            try
            {
                sealedBytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }

            var session = Open(sealedBytes);
            if (session is null) DeleteQuietly();
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(ClientSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var plain = JsonSerializer.SerializeToUtf8Bytes(
            new StoredSession(session.AccessToken, session.RefreshToken, session.AccessTokenExpiresAt),
            StorageJsonContext.Default.StoredSession);
        try
        {
            var sealedBytes = _protector.Protect(plain);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                // Write beside the target, then swap, so a crash mid-write never leaves half a file.
                var temporary = _path + ".tmp";
                await File.WriteAllBytesAsync(temporary, sealedBytes, cancellationToken).ConfigureAwait(false);
                // Owner-only where the file system has modes (the bytes are sealed, but nobody else needs to read them).
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DeleteQuietly();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private ClientSession? Open(byte[] sealedBytes)
    {
        byte[]? plain = null;
        try
        {
            plain = _protector.Unprotect(sealedBytes);
            if (plain is null) return null;

            var stored = JsonSerializer.Deserialize(plain, StorageJsonContext.Default.StoredSession);
            return stored is { AccessToken.Length: > 0, RefreshToken.Length: > 0 }
                ? new ClientSession(stored.AccessToken, stored.RefreshToken, stored.AccessTokenExpiresAt)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (plain is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void DeleteQuietly()
    {
        try
        {
            File.Delete(_path);
            File.Delete(_path + ".tmp");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file we cannot delete is no worse than before; the next load reads it the same way.
        }
    }
}

internal sealed record StoredSession(
    [property: JsonPropertyName("access")] string AccessToken,
    [property: JsonPropertyName("refresh")] string RefreshToken,
    [property: JsonPropertyName("expires")] DateTimeOffset AccessTokenExpiresAt);

[JsonSerializable(typeof(StoredSession))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
