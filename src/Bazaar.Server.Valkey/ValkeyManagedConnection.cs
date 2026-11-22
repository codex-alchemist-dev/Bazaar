using System.Security.Cryptography;
using System.Text;
using Bazaar.Secrets;

namespace Bazaar.Server.Valkey;

/// <summary>
/// The connection of the managed child: a password generated for this host run, held as a <see cref="SecretValue"/> (redacted in
/// <c>ToString</c>, zeroed on dispose), and the port the supervisor chose for the current child. Clients get a connection string only
/// while the child is ready. The password is never written to a file, a log or a command line; it reaches the child on stdin.
/// </summary>
public sealed class ValkeyManagedConnection : IValkeyConnectionSource, IDisposable
{
    private readonly SecretValue _password;
    private volatile int _port;
    private volatile bool _ready;

    /// <summary>Generates a fresh 256-bit password for this host run. A restart of the child keeps it, so cached clients stay valid.</summary>
    public ValkeyManagedConnection()
    {
        Span<byte> raw = stackalloc byte[32];
        RandomNumberGenerator.Fill(raw);
        var text = Convert.ToHexString(raw);
        CryptographicOperations.ZeroMemory(raw);
        _password = SecretValue.FromText(text);
    }

    /// <summary>The port of the current child, or 0 before the first start.</summary>
    public int Port => _port;

    /// <summary>True between a successful readiness ping and the child's exit.</summary>
    public bool IsReady => _ready;

    /// <inheritdoc />
    public ValueTask<string> GetConnectionStringAsync(CancellationToken ct)
    {
        if (!_ready)
        {
            throw new InvalidOperationException("The managed Valkey server is not running.");
        }

        return ValueTask.FromResult(BuildConnectionString(_port));
    }

    /// <summary>Records the port for the child about to start. Not ready until <see cref="MarkReady"/>.</summary>
    internal void Prepare(int port)
    {
        _ready = false;
        _port = port;
    }

    /// <summary>Called when the child answers its first ping.</summary>
    internal void MarkReady() => _ready = true;

    /// <summary>Called when the child stops or fails.</summary>
    internal void MarkUnavailable() => _ready = false;

    /// <summary>The password in plain text, for the child's configuration only.</summary>
    internal string PasswordForChildConfig() => Encoding.UTF8.GetString(_password.Bytes);

    /// <summary>The loopback connection string for <paramref name="port"/>. Used by the supervisor before the child is ready.</summary>
    internal string BuildConnectionString(int port) => $"127.0.0.1:{port},password={PasswordForChildConfig()}";

    /// <inheritdoc />
    public void Dispose() => _password.Dispose();

    /// <summary>Never includes the password.</summary>
    public override string ToString() => "managed Valkey connection (password redacted)";
}
