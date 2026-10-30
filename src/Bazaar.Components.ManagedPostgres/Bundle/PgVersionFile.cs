using System.Globalization;

namespace Bazaar.Components.ManagedPostgres.Bundle;

/// <summary>
/// Reads and writes the <c>pg-version</c> file inside a cluster data directory. The file holds the major as integer text.
/// It is written after initdb, because initdb requires an empty data directory.
/// </summary>
public static class PgVersionFile
{
    /// <summary>File name inside the cluster data directory.</summary>
    public const string FileName = "pg-version";

    /// <summary>Recorded major, or null when the file does not exist.</summary>
    /// <exception cref="InvalidDataException">The file exists but does not hold a positive integer.</exception>
    public static int? Read(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
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

    /// <summary>Records the major, creating the data directory if needed.</summary>
    public static void Write(string dataDirectory, int major)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(major);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(dataDirectory, FileName), major.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
    }
}
