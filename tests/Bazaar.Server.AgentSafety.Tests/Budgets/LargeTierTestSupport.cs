using System.Text;
using Bazaar.Server.Data.Migrations;
using Npgsql;

namespace Bazaar.Server.AgentSafety.Tests.Budgets;

/// <summary>Shared helpers for the large token amount tier tests (doc 48, DECIDED 11.4).</summary>
internal static class LargeTierTestSupport
{
    /// <summary>Rows in the spend table, the large headers and the large chunks.</summary>
    public static async Task<(long Spends, long Headers, long Chunks)> CountsAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT (SELECT count(*) FROM agent_token_spend), (SELECT count(*) FROM token_amount_large), (SELECT count(*) FROM token_amount_large_chunk)");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>A seeded random digit string whose first digit is not zero.</summary>
    public static string RandomDigits(Random random, int length, bool nonZeroFirst = true, bool nonZeroLast = false)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('0' + random.Next(10));
        }

        if (nonZeroFirst && length > 0)
        {
            chars[0] = (char)('1' + random.Next(9));
        }

        if (nonZeroLast && length > 0)
        {
            chars[^1] = (char)('1' + random.Next(9));
        }

        return new string(chars);
    }

    /// <summary>Twice a plain decimal, by schoolbook doubling on the text: an independent reference for a streamed sum.</summary>
    public static string Double(string text)
    {
        var point = text.IndexOf('.');
        var digits = (point < 0 ? text : text.Remove(point, 1)).ToCharArray();
        var result = new char[digits.Length + 1];
        var carry = 0;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var value = ((digits[i] - '0') * 2) + carry;
            result[i + 1] = (char)('0' + (value % 10));
            carry = value / 10;
        }

        result[0] = (char)('0' + carry);
        var all = new StringBuilder().Append(result).ToString();
        var fractionLength = point < 0 ? 0 : text.Length - point - 1;
        var integer = all[..(all.Length - fractionLength)].TrimStart('0');
        var fraction = all[(all.Length - fractionLength)..].TrimEnd('0');
        integer = integer.Length == 0 ? "0" : integer;
        return fraction.Length == 0 ? integer : integer + "." + fraction;
    }

    /// <summary>The server's migrations without one file, to build a database as it stood before that file.</summary>
    public sealed class WithoutMigration(IMigrationSource inner, string ns, int version) : IMigrationSource
    {
        public IReadOnlyList<string> Namespaces() => inner.Namespaces();

        public IReadOnlyList<MigrationFile> Load(string loadNs) =>
            [.. inner.Load(loadNs).Where(file => !(file.Namespace == ns && file.Version == version))];
    }
}
