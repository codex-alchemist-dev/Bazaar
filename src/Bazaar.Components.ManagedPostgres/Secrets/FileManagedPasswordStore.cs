namespace Bazaar.Components.ManagedPostgres.Secrets;

/// <summary>
/// Keeps the managed superuser password in a file that only the service account can read (mode 0600, inside a 0700 folder).
/// This is the store on Linux and macOS. Windows uses DPAPI instead (<c>DpapiManagedPasswordStore</c>), so this class refuses to
/// run there: a plain file on Windows would be readable by other accounts. A Windows KEK-backed store can slot in behind
/// <see cref="IManagedPasswordStore"/> later without touching callers.
/// </summary>
public sealed class FileManagedPasswordStore : IManagedPasswordStore
{
    private readonly string _path;

    public FileManagedPasswordStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The managed password file store is for Linux and macOS. Windows uses the DPAPI store.");
        }

        _path = path;
    }

    public Task<string?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Task.FromResult<string?>(null);
        }

        var text = File.ReadAllText(_path).Trim();
        return Task.FromResult<string?>(text.Length == 0 ? null : text);
    }

    public Task SaveAsync(string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var folder = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Write beside the target, set the mode while the file is still empty, then move into place. A crash never leaves a half-written store.
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using var writer = new StreamWriter(stream);
            writer.Write(password);
        }

        File.Move(temp, _path, overwrite: true);
        return Task.CompletedTask;
    }
}
