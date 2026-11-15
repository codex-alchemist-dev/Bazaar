using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bazaar.Secrets.Keyring;

namespace Bazaar.Secrets.Tpm;

/// <summary>
/// KEK sealed by a TPM (tpm2-tools). On first use a random 32-byte key is sealed under the owner-hierarchy primary, and the public
/// and private blobs are saved as <c>{keyId}.pub</c> and <c>{keyId}.priv</c> in the state directory. Later starts load and unseal
/// those blobs, so the plaintext KEK exists only in memory. Wrapping then runs in process via <see cref="SoftwareKekProvider"/>.
/// The blobs alone are useless without this TPM. Only a clean "neither blob exists" creates a key; a half-written state throws,
/// because creating over it would orphan every scope wrapped under the old key.
/// </summary>
public sealed partial class TpmKekProvider : IKekProvider, IDisposable
{
    private const int _keyBytes = 32;

    private readonly SoftwareKekProvider _inner;

    private TpmKekProvider(SoftwareKekProvider inner) => _inner = inner;

    public string CurrentKeyId => _inner.CurrentKeyId;

    public Task<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken ct) =>
        _inner.WrapAsync(key, associatedData, ct);

    public Task<ReadOnlyMemory<byte>> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> associatedData, CancellationToken ct) =>
        _inner.UnwrapAsync(keyId, wrapped, associatedData, ct);

    public void Dispose() => _inner.Dispose();

    /// <summary>Opens the TPM-sealed KEK for <paramref name="keyId"/>, creating and sealing it on first use.</summary>
    /// <exception cref="KekCustodyException">The TPM is unavailable, the blobs are half-written, or the unseal does not return a 32-byte key.</exception>
    public static async Task<TpmKekProvider> OpenAsync(ITpmToolRunner runner, string stateDirectory, string keyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        if (!KeyIdPattern().IsMatch(keyId))
        {
            throw new ArgumentException("TPM KEK ids are 1 to 63 lowercase letters, digits or hyphens.", nameof(keyId));
        }

        Directory.CreateDirectory(stateDirectory);
        var pubPath = Path.Combine(stateDirectory, keyId + ".pub");
        var privPath = Path.Combine(stateDirectory, keyId + ".priv");
        var hasPub = File.Exists(pubPath);
        var hasPriv = File.Exists(privPath);

        byte[] key;
        if (hasPub && hasPriv)
        {
            key = await UnsealAsync(runner, stateDirectory, pubPath, privPath, keyId, ct).ConfigureAwait(false);
        }
        else if (!hasPub && !hasPriv)
        {
            key = await CreateAndSealAsync(runner, stateDirectory, pubPath, privPath, keyId, ct).ConfigureAwait(false);
        }
        else
        {
            throw new KekCustodyException($"TPM KEK '{keyId}' is half-written: one of its blobs is missing. Restore both blobs or remove the remaining one by hand.");
        }

        try
        {
            return new TpmKekProvider(new SoftwareKekProvider(keyId, key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<byte[]> CreateAndSealAsync(ITpmToolRunner runner, string dir, string pubPath, string privPath, string keyId, CancellationToken ct)
    {
        var fresh = RandomNumberGenerator.GetBytes(_keyBytes);
        var work = NewWorkDirectory(dir);
        try
        {
            await FlushAsync(runner, work, ct).ConfigureAwait(false);
            await RunChecked(runner, "tpm2_createprimary", ["-C", "o", "-g", "sha256", "-G", "rsa", "-c", "primary.ctx"], null, work, ct).ConfigureAwait(false);
            await FlushAsync(runner, work, ct).ConfigureAwait(false);
            await RunChecked(runner, "tpm2_create", ["-C", "primary.ctx", "-i", "-", "-u", "seal.pub", "-r", "seal.priv"], fresh, work, ct).ConfigureAwait(false);
            await FlushAsync(runner, work, ct).ConfigureAwait(false);

            // Commit the blobs before proving them, so a crash after this point leaves a usable key rather than an orphan.
            CopyAtomically(Path.Combine(work, "seal.pub"), pubPath);
            CopyAtomically(Path.Combine(work, "seal.priv"), privPath);

            var readBack = await UnsealAsync(runner, dir, pubPath, privPath, keyId, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(readBack, fresh))
            {
                CryptographicOperations.ZeroMemory(readBack);
                throw new KekCustodyException($"TPM KEK '{keyId}' unsealed to a different key than was sealed.");
            }

            return readBack;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fresh);
            DeleteWorkDirectory(work);
        }
    }

    private static async Task<byte[]> UnsealAsync(ITpmToolRunner runner, string dir, string pubPath, string privPath, string keyId, CancellationToken ct)
    {
        var work = NewWorkDirectory(dir);
        try
        {
            File.Copy(pubPath, Path.Combine(work, "seal.pub"));
            File.Copy(privPath, Path.Combine(work, "seal.priv"));
            await FlushAsync(runner, work, ct).ConfigureAwait(false);
            await RunChecked(runner, "tpm2_createprimary", ["-C", "o", "-g", "sha256", "-G", "rsa", "-c", "primary.ctx"], null, work, ct).ConfigureAwait(false);
            await FlushAsync(runner, work, ct).ConfigureAwait(false);
            await RunChecked(runner, "tpm2_load", ["-C", "primary.ctx", "-u", "seal.pub", "-r", "seal.priv", "-c", "seal.ctx"], null, work, ct).ConfigureAwait(false);
            await FlushAsync(runner, work, ct).ConfigureAwait(false);
            var unsealed = await RunChecked(runner, "tpm2_unseal", ["-c", "seal.ctx"], null, work, ct).ConfigureAwait(false);
            if (unsealed.StandardOutput.Length != _keyBytes)
            {
                CryptographicOperations.ZeroMemory(unsealed.StandardOutput);
                throw new KekCustodyException($"TPM KEK '{keyId}' did not unseal to {_keyBytes} bytes.");
            }

            return unsealed.StandardOutput;
        }
        finally
        {
            DeleteWorkDirectory(work);
        }
    }

    private static async Task<TpmToolResult> RunChecked(ITpmToolRunner runner, string tool, IReadOnlyList<string> args, byte[]? stdin, string work, CancellationToken ct)
    {
        var result = await runner.RunAsync(tool, args, stdin, work, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new KekCustodyException($"{tool} failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result;
    }

    /// <summary>Flushes transient objects. Skipping this between steps exhausts the TPM's object slots on a long session.</summary>
    private static async Task FlushAsync(ITpmToolRunner runner, string work, CancellationToken ct) =>
        await RunChecked(runner, "tpm2_flushcontext", ["-t"], null, work, ct).ConfigureAwait(false);

    private static string NewWorkDirectory(string dir)
    {
        var work = Path.Combine(dir, "tpm-work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private static void DeleteWorkDirectory(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the work directory holds only public blobs and context handles, never the key.
        }
    }

    private static void CopyAtomically(string source, string destination)
    {
        var temp = destination + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();
}
