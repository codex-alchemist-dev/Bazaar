using System.Globalization;

namespace Bazaar.Components.ManagedPostgres.Bundle;

/// <summary>
/// Reads and writes the <c>pg-version</c> file. It sits beside the cluster's data directory, in <c>{dataRoot}/pg/{major}/</c>,
/// so initdb still finds the data directory empty. The file holds the major as integer text.
/// </summary>
public static class PgVersionFile
{
    /// <summary>File name beside the cluster data directory.</summary>
    public const string FileName = "pg-version";

    /// <summary>Recorded major, or null when the file does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but does not hold a positive integer.</exception>
    public static int? Read(string clusterRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterRoot);
        var path = Path.Combine(clusterRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path).Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var major) || major <= 0)
        {
            throw new InvalidDataException($"The pg-version file at '{path}' does not hold a PostgreSQL major version.");
        }

        return major;
    }

    /// <summary>Records the major, creating the cluster root if needed.</summary>
    public static void Write(string clusterRoot, int major)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        Directory.CreateDirectory(clusterRoot);
        File.WriteAllText(Path.Combine(clusterRoot, FileName), major.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
    }
}
