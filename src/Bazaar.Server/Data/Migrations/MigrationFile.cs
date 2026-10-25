using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Data.Migrations;

/// <summary>One versioned SQL file. <see cref="Provider"/> is the folder it came from: <c>common</c>, <c>windows</c> or <c>linux</c>.</summary>
public sealed record MigrationFile(string Provider, int Version, string Name, string Sql)
{
    /// <summary>Marker on the first line for files that must run outside a transaction (<c>CREATE INDEX CONCURRENTLY</c>).</summary>
    public const string NoTransactionMarker = "-- @no-transaction";

    /// <summary>True when the file asks to run outside a transaction. Such a file must hold a single statement.</summary>
    public bool NoTransaction { get; } = FirstLine(Sql).Trim().Equals(NoTransactionMarker, StringComparison.Ordinal);

    /// <summary>SHA-256 (lowercase hex) of the SQL with line endings normalised to LF, so a Windows checkout hashes the same.</summary>
    public string Checksum { get; } = ComputeChecksum(Sql);

    public static string ComputeChecksum(string sql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql.Replace("\r\n", "\n", StringComparison.Ordinal))));

    /// <summary>Parses <c>NNNN_name.sql</c>; returns false when the name does not follow the convention.</summary>
    public static bool TryParseFileName(string fileName, out int version, out string name)
    {
        version = 0;
        name = "";
        if (!fileName.EndsWith(".sql", StringComparison.Ordinal))
            return false;
        var stem = fileName[..^4];
        var underscore = stem.IndexOf('_', StringComparison.Ordinal);
        if (underscore != 4 || stem.Length == 5 || !int.TryParse(stem.AsSpan(0, 4), out version))
            return false;
        name = stem[5..];
        return true;
    }

    private static string FirstLine(string sql)
    {
        var end = sql.IndexOf('\n', StringComparison.Ordinal);
        return end < 0 ? sql : sql[..end];
    }
}
