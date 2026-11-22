using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Bazaar.Server.Community.Observatory;

/// <summary>A place in the log order: rows are sorted by (created_at, id) descending, and id breaks ties between equal times.</summary>
/// <param name="CreatedAt">created_at of the row.</param>
/// <param name="Id">id of the row.</param>
public readonly record struct CommunityLogPosition(DateTimeOffset CreatedAt, long Id);

/// <summary>
/// Opaque page cursor of the community log: base64url of <c>v1.&lt;utc ticks&gt;.&lt;id&gt;</c>. It is validated, not signed:
/// it names a log position only, and the route is owner-only, so a forged cursor reveals nothing a first page would not.
/// </summary>
public static class CommunityLogCursor
{
    private const string _version = "v1";
    private const int _maxLength = 96;

    /// <summary>Encodes a position into a cursor string.</summary>
    public static string Encode(CommunityLogPosition position)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{_version}.{position.CreatedAt.UtcTicks}.{position.Id}");
        return Base64Url.EncodeToString(Encoding.ASCII.GetBytes(text));
    }

    /// <summary>Decodes a cursor. Returns false for anything that is not a cursor this class produced; never throws.</summary>
    public static bool TryDecode(string? cursor, out CommunityLogPosition position)
    {
        position = default;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > _maxLength || !Base64Url.IsValid(cursor))
            return false;

        string text;
        try
        {
            text = Encoding.ASCII.GetString(Base64Url.DecodeFromChars(cursor));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3 || parts[0] != _version
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || ticks > DateTime.MaxValue.Ticks || id < 1)
            return false;

        position = new CommunityLogPosition(new DateTimeOffset(ticks, TimeSpan.Zero), id);
        return true;
    }
}
