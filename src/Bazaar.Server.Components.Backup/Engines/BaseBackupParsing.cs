using System.Text.RegularExpressions;

namespace Bazaar.Server.Components.Backup.Engines;

/// <summary>Reads the start WAL segment name from a base backup's <c>backup_label</c> file.</summary>
public static partial class BaseBackupParsing
{
    [GeneratedRegex(@"^START WAL LOCATION: \S+ \(file ([0-9A-F]{24})\)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StartWalLine();

    /// <summary>The 24-hex-digit WAL segment name, or null when the label is missing or unreadable.</summary>
    public static string? ReadStartWal(string backupDirectory)
    {
        var label = Path.Combine(backupDirectory, "backup_label");
        if (!File.Exists(label))
            return null;
        var match = StartWalLine().Match(File.ReadAllText(label));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Total size in bytes of every file under <paramref name="directory"/>.</summary>
    public static long SizeOf(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
}
