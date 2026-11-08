using System.Globalization;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Agents.Data;

/// <summary>Helpers shared by the agent runtime repositories: snake_case enum text and jsonb parameters.</summary>
internal static class AgentSql
{
    public static string ToDb<T>(T value) where T : struct, Enum
    {
        var name = value.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    public static T FromDb<T>(string value) where T : struct, Enum
    {
        var pascal = new StringBuilder(value.Length);
        foreach (var part in value.Split('_'))
            pascal.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        return Enum.TryParse<T>(pascal.ToString(), ignoreCase: false, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unknown {typeof(T).Name} value '{value}'.");
    }

    public static NpgsqlParameter Jsonb(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };

    public static object Nullable<T>(T? value) where T : struct => value.HasValue ? value.Value : DBNull.Value;

    public static object NullableRef(string? value) => (object?)value ?? DBNull.Value;
}
