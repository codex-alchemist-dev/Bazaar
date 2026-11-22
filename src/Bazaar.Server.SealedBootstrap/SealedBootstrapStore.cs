using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedBootstrap;

/// <summary>
/// <see cref="ISealedBootstrapStore"/> over <see cref="ISealedStore"/> (doc 61 work package 5). Values live at <c>values/&lt;name&gt;</c>
/// in scope <see cref="Scope"/>. Configured imports (<see cref="SealedBootstrapOptions.ImportFrom"/>) run before the first operation; a
/// failed import fails that operation and is retried by the next one, so no value is read while its plaintext source should still be
/// sealed. Audit entries name the value, never its content.
/// </summary>
public sealed partial class SealedBootstrapStore : ISealedBootstrapStore, IDisposable
{
    /// <summary>The sealed scope of host bootstrap values (doc 61 inventory).</summary>
    public const string Scope = "host:bootstrap";

    /// <summary>The largest bootstrap value or import file, in bytes.</summary>
    public const int MaxValueBytes = 64 * 1024;

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly ISealedStore _store;
    private readonly ISealAuditSink _audit;
    private readonly SealedBootstrapOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private volatile bool _importsDone;

    public SealedBootstrapStore(ISealedStore store, ISealAuditSink audit, SealedBootstrapOptions? options = null, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? new SealedBootstrapOptions();
        _time = time ?? TimeProvider.System;
        foreach (var (name, path) in _options.ImportFrom)
        {
            ValidateName(name);
            if (!Path.IsPathFullyQualified(path))
                throw new ArgumentException($"Import source for '{name}' must be an absolute path.", nameof(options));
        }
    }

    public async ValueTask<string> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await EnsureImportsAsync(cancellationToken).ConfigureAwait(false);
        using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
        var file = await scope.ReadAsync(PathOf(name), cancellationToken).ConfigureAwait(false)
            ?? throw new SealedBootstrapValueNotFoundException(name);
        try
        {
            return _strictUtf8.GetString(file.Content);
        }
        catch (DecoderFallbackException)
        {
            // Authenticated bytes that are not UTF-8 were written by something other than this module: fail closed.
            throw new SealTamperedException(Scope, PathOf(name), "Sealed bootstrap value is not UTF-8.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(file.Content);
        }
    }

    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var bytes = ValueBytes(value);
        try
        {
            await EnsureImportsAsync(cancellationToken).ConfigureAwait(false);
            using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
            await RecordAsync("bootstrap.set", name, "Bootstrap value stored.", cancellationToken).ConfigureAwait(false);
            await scope.WriteAsync(PathOf(name), bytes, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        await EnsureImportsAsync(cancellationToken).ConfigureAwait(false);
        using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
        await RecordAsync("bootstrap.remove", name, "Bootstrap value removed.", cancellationToken).ConfigureAwait(false);
        return await scope.DeleteAsync(PathOf(name), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureImportsAsync(cancellationToken).ConfigureAwait(false);
        using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
        var entries = await scope.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. entries.Where(e => e.Path.StartsWith("values/", StringComparison.Ordinal)).Select(e => e.Path["values/".Length..]).Order(StringComparer.Ordinal)];
    }

    public async ValueTask<SealedBootstrapImportResult> ImportPlaintextFileAsync(string name, string path, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The import source must be an absolute path.", nameof(path));

        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The bootstrap import source does not exist.", path);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            await RecordAsync("bootstrap.import.refused", name, "Import source is a link; nothing was sealed or wiped.", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Bootstrap import source for '{name}' is a link. Point the import at the real file.");
        }

        if (info.Length > MaxValueBytes)
            throw new InvalidOperationException($"Bootstrap import source for '{name}' is larger than {MaxValueBytes} bytes.");

        var raw = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[] value;
        try
        {
            // The same trimming as the file: resolver, so the sealed value equals what the reference used to resolve to.
            var text = _strictUtf8.GetString(raw).TrimEnd('\r', '\n');
            value = ValueBytes(text);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException($"Bootstrap import source for '{name}' is not UTF-8 text.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }

        try
        {
            using var scope = await _store.OpenScopeAsync(Scope, cancellationToken).ConfigureAwait(false);
            var logical = PathOf(name);
            var existing = await scope.ReadAsync(logical, cancellationToken).ConfigureAwait(false);
            SealedBootstrapImportResult result;
            if (existing is not null)
            {
                var same = CryptographicOperations.FixedTimeEquals(existing.Content, value);
                CryptographicOperations.ZeroMemory(existing.Content);
                if (!same)
                {
                    await RecordAsync("bootstrap.import.refused", name, "A different value is already sealed under this name; the plaintext was kept.", cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException($"A different bootstrap value is already sealed as '{name}'. Remove it first or drop the import; the plaintext file was kept.");
                }

                await RecordAsync("bootstrap.import.already_done", name, "Value already sealed; wiping the plaintext source.", cancellationToken).ConfigureAwait(false);
                result = SealedBootstrapImportResult.AlreadyImported;
            }
            else
            {
                await RecordAsync("bootstrap.import", name, "Sealing a plaintext bootstrap value.", cancellationToken).ConfigureAwait(false);
                await scope.WriteAsync(logical, value, 0, cancellationToken).ConfigureAwait(false);
                var back = await scope.ReadAsync(logical, cancellationToken).ConfigureAwait(false);
                var verified = back is not null && CryptographicOperations.FixedTimeEquals(back.Content, value);
                if (back is not null)
                    CryptographicOperations.ZeroMemory(back.Content);
                if (!verified)
                {
                    await scope.DeleteAsync(logical, cancellationToken).ConfigureAwait(false);
                    throw new SealTamperedException(Scope, logical, "Imported bootstrap value did not read back identically; plaintext kept.");
                }

                result = SealedBootstrapImportResult.Imported;
            }

            await WipeAsync(name, path, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    /// <summary>Releases the import gate.</summary>
    public void Dispose() => _importGate.Dispose();

    /// <summary>The value name rule: lower-case letters, digits, '.', '_' and '-', 1 to 64 characters, starting with a letter or digit. KEK names are refused.</summary>
    public static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException($"Bootstrap value name '{name}' must be 1 to 64 of a-z, 0-9, '.', '_', '-' and start with a letter or digit.", nameof(name));

        // Circularity guard (doc 61 "Trust chain"): this scope is wrapped by the KEK, so the KEK and its reference cannot live in it.
        if (name.StartsWith("kek", StringComparison.Ordinal) || name.StartsWith("identity.kek", StringComparison.Ordinal))
            throw new ArgumentException($"'{name}' names the KEK, which cannot be stored in a scope the KEK wraps (doc 61).", nameof(name));
    }

    private async ValueTask EnsureImportsAsync(CancellationToken cancellationToken)
    {
        if (_importsDone)
            return;

        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_importsDone)
                return;

            foreach (var (name, path) in _options.ImportFrom.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // Once the source is gone the import is done; the setting is then ignored (doc 61).
                if (File.Exists(path))
                    await ImportPlaintextFileAsync(name, path, cancellationToken).ConfigureAwait(false);
            }

            _importsDone = true;
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async ValueTask WipeAsync(string name, string path, CancellationToken cancellationToken)
    {
        try
        {
            var length = new FileInfo(path).Length;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(length, 8192)];
                for (long written = 0; written < length; written += zeros.Length)
                {
                    await stream.WriteAsync(zeros.AsMemory(0, (int)Math.Min(zeros.Length, length - written)), cancellationToken).ConfigureAwait(false);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await RecordAsync("bootstrap.import.wipe_failed", name, "The value is sealed but its plaintext source could not be wiped.", CancellationToken.None).ConfigureAwait(false);
            throw new IOException($"Bootstrap value '{name}' is sealed but its plaintext source could not be wiped; remove it by hand. The next start retries the wipe.", ex);
        }
    }

    private ValueTask RecordAsync(string kind, string name, string detail, CancellationToken cancellationToken) =>
        _audit.RecordAsync(new SealAuditEvent(_time.GetUtcNow(), kind, Scope, PathOf(name), detail), cancellationToken);

    private static string PathOf(string name) => "values/" + name;

    private static byte[] ValueBytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A bootstrap value cannot be empty.", nameof(value));

        var bytes = _strictUtf8.GetBytes(value);
        if (bytes.Length > MaxValueBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException($"A bootstrap value is at most {MaxValueBytes} bytes.", nameof(value));
        }

        return bytes;
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
