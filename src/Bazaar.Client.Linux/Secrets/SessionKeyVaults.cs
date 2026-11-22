using System.Security.Cryptography;

namespace Bazaar.Client.Linux.Secrets;

/// <summary>Holds the session key in the freedesktop Secret Service through <c>secret-tool</c>. The key travels on stdin only.</summary>
internal static class SecretToolKeyVault
{
    private static readonly string[] _attributes = ["service", "bazaar", "kind", "session-key"];
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(10);

    /// <summary>The stored key, creating and storing one when the keyring has none. Null when no Secret Service can be used.</summary>
    public static byte[]? TryGetOrCreate(ISecretToolRunner runner)
    {
        try
        {
            // Off the caller's thread, so a UI synchronisation context cannot deadlock the wait.
            return Task.Run(() => GetOrCreateAsync(runner)).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is SecretStoreUnavailableException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> GetOrCreateAsync(ISecretToolRunner runner)
    {
        using var budget = new CancellationTokenSource(_budget);

        var lookup = await runner.RunAsync(["lookup", .. _attributes], null, budget.Token).ConfigureAwait(false);
        if (lookup.ExitCode == 0 && TryDecode(lookup.StandardOutput, out var existing)) return existing;

        var key = RandomNumberGenerator.GetBytes(32);
        var store = await runner.RunAsync(["store", "--label=Bazaar session key", .. _attributes], Convert.ToBase64String(key), budget.Token).ConfigureAwait(false);
        if (store.ExitCode != 0)
        {
            CryptographicOperations.ZeroMemory(key);
            return null;
        }

        return key;
    }

    private static bool TryDecode(string text, out byte[] key)
    {
        key = [];
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length != 32) return false;
            key = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The honest fallback: a 32-byte key in its own file, mode 0600 in a 0700 directory.</summary>
internal static class FileKeyVault
{
    public static byte[]? TryGetOrCreate(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32)
                {
                    Tighten(path);
                    return existing;
                }
                // A damaged key cannot open anything it sealed; a fresh one signs the person out rather than failing.
            }

            var key = RandomNumberGenerator.GetBytes(32);
            var temporary = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(key);
            }

            File.Move(temporary, path, overwrite: true);
            Tighten(path);
            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Tighten(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
