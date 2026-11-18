namespace Bazaar.Diagnostics.Capture;

/// <summary>
/// The crash-on-last-run marker (doc 43 section 7.5). The client calls <see cref="Begin"/> at start and
/// <see cref="End"/> on clean shutdown. If the file is still there at the next start, the last run did not exit
/// cleanly. The path is injected, so tests use a temporary directory.
/// </summary>
public sealed class CrashSentinel
{
    private readonly string _path;

    /// <param name="path">The sentinel file path. Its parent directory is created on <see cref="Begin"/> when missing.</param>
    public CrashSentinel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    /// <summary>
    /// Writes the sentinel for this run.
    /// </summary>
    /// <returns>True when a sentinel from an earlier run was still present, so that run did not shut down cleanly.</returns>
    public bool Begin()
    {
        var previousRunUnclean = File.Exists(_path);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_path, string.Empty);
        return previousRunUnclean;
    }

    /// <summary>Removes the sentinel on clean shutdown. Does nothing when it is already gone.</summary>
    public void End()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
