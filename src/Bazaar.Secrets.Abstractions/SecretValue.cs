using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Secrets;

/// <summary>
/// A plaintext secret in memory. It redacts itself in <see cref="ToString"/>, and its bytes are zeroed on
/// <see cref="Dispose"/>. Callers dispose it as soon as the secret has been used.
/// </summary>
public sealed class SecretValue : IDisposable
{
    private readonly byte[] _bytes;
    private bool _disposed;

    /// <summary>Copies <paramref name="bytes"/>; the caller keeps and zeroes its own buffer.</summary>
    public SecretValue(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException("A secret value cannot be empty.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    public static SecretValue FromText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var raw = Encoding.UTF8.GetBytes(text);
        try
        {
            return new SecretValue(raw);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_bytes);
            _disposed = true;
        }
    }

    public override string ToString() => "[redacted]";
}
