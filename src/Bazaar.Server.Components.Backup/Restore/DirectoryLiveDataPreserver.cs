using System.Globalization;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Components.Backup.Restore;

/// <summary>
/// Moves live data directories aside before an owner-confirmed overwrite (doc 48 section 6). Each directory is renamed to a
/// sibling named <c>{name}.preserved-{UTC timestamp}-{restoreId}</c>. Nothing is ever deleted, and an existing target is
/// never overwritten. Which directories count as live data (Soul folders, Vault checkout, and so on) is the host's choice
/// and is passed in.
/// </summary>
public sealed partial class DirectoryLiveDataPreserver : ILiveDataPreserver
{
    private readonly List<string> _directories;
    private readonly TimeProvider _time;

    /// <param name="directories">The live data directories to move aside. A directory that does not exist is skipped.</param>
    /// <param name="time">The clock for the folder timestamp.</param>
    public DirectoryLiveDataPreserver(IEnumerable<string> directories, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        _directories = directories.Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d))).Distinct(StringComparer.Ordinal).ToList();
        if (_directories.Count == 0)
        {
            throw new ArgumentException("At least one live data directory is required.", nameof(directories));
        }

        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Moves every existing configured directory aside. All targets are checked before the first move, so a collision
    /// changes nothing.
    /// </summary>
    /// <exception cref="ArgumentException">The restore id is not made of letters, digits, '-' and '_'.</exception>
    /// <exception cref="IOException">A target already exists, or a directory contains another configured one.</exception>
    public Task PreserveAsync(string restoreId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(restoreId);
        if (!SafeId().IsMatch(restoreId))
        {
            throw new ArgumentException("The restore id may only contain letters, digits, '-' and '_'.", nameof(restoreId));
        }

        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var plan = new List<(string Source, string Target)>();
        foreach (var source in _directories)
        {
            if (!Directory.Exists(source))
            {
                continue;
            }

            var parent = Path.GetDirectoryName(source) ?? throw new IOException("A live data directory cannot be a filesystem root.");
            var target = Path.Combine(parent, $"{Path.GetFileName(source)}.preserved-{stamp}-{restoreId}");
            if (Directory.Exists(target) || File.Exists(target))
            {
                throw new IOException("The preserved-data folder already exists and will not be overwritten.");
            }

            plan.Add((source, target));
        }

        foreach (var (source, _) in plan)
        {
            foreach (var (other, _) in plan)
            {
                if (!ReferenceEquals(source, other) && IsInside(other, source))
                {
                    throw new IOException("A live data directory lies inside another one, so they cannot be moved aside separately.");
                }
            }
        }

        foreach (var (source, target) in plan)
        {
            ct.ThrowIfCancellationRequested();
            Directory.Move(source, target);
            DurableFile.FsyncDirectory(Path.GetDirectoryName(target)!);
        }

        return Task.CompletedTask;
    }

    private static bool IsInside(string path, string parent) =>
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\z")]
    private static partial Regex SafeId();
}
