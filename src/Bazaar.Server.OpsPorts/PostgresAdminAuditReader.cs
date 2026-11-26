using System.Text;
using System.Text.Json;
using Bazaar.Server.WebAdmin;
using Npgsql;

namespace Bazaar.Server.OpsPorts;

/// <summary>
/// The doc 51 audit view over the <c>audit_log</c> table (migration 0004, read only). Newest first, paged by sequence number. The
/// summary is a bounded one-line digest of the payload's top-level fields: nested values are left out, and any field whose name looks
/// like a secret is shown as <c>***</c>, so a payload that wrongly carried one still does not reach the screen.
/// </summary>
public sealed class PostgresAdminAuditReader(NpgsqlDataSource dataSource) : IAdminAuditReader
{
    public const int MaxLimit = 200;
    public const int MaxSummaryLength = 160;

    private const string _sql =
        "SELECT seq, ts, actor, kind, payload::text FROM audit_log " +
        "WHERE ($1::bigint IS NULL OR seq < $1) ORDER BY seq DESC LIMIT $2";

    public async Task<AdminAuditPage> ReadAsync(long? before, int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxLimit);
        var rows = new List<AdminAuditRow>(take);
        await using var command = dataSource.CreateCommand(_sql);
        command.Parameters.Add(new NpgsqlParameter<long?> { TypedValue = before, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = take });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new AdminAuditRow(
                reader.GetInt64(0),
                new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
                reader.GetString(2),
                reader.GetString(3),
                Summarize(reader.GetString(4))));
        }

        return new AdminAuditPage(rows, rows.Count == take ? rows[^1].Seq : null);
    }

    /// <summary>The digest of one payload. Never throws: text that is not a JSON object digests to an empty string.</summary>
    internal static string Summarize(string payloadJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    continue;
                }

                var value = LooksSecret(property.Name) ? "***" : Clean(property.Value.ToString());
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(property.Name).Append('=').Append(value);
                if (builder.Length >= MaxSummaryLength)
                {
                    break;
                }
            }

            return builder.Length <= MaxSummaryLength ? builder.ToString() : builder.ToString(0, MaxSummaryLength - 1) + "…";
        }
    }

    private static bool LooksSecret(string name)
    {
        foreach (var word in _secretWords)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly string[] _secretWords = ["secret", "token", "cookie", "password", "passphrase", "key", "code", "credential", "bearer"];

    /// <summary>Payload text is untrusted: control characters (an escape sequence in a name, say) never reach the terminal.</summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }
}
