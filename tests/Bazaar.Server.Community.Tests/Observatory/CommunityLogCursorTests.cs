using System.Buffers.Text;
using System.Text;
using Bazaar.Server.Community.Observatory;

namespace Bazaar.Server.Community.Tests.Observatory;

/// <summary>Checks the opaque page cursor: round trip, and that malformed input is refused without throwing.</summary>
public class CommunityLogCursorTests
{
    [Fact]
    public void Position_round_trips_with_full_tick_precision()
    {
        var position = new CommunityLogPosition(new DateTimeOffset(2026, 11, 20, 8, 30, 15, TimeSpan.Zero).AddTicks(1234560), 987654321012L);

        var cursor = CommunityLogCursor.Encode(position);

        Assert.True(CommunityLogCursor.TryDecode(cursor, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void Offset_times_are_normalised_to_utc()
    {
        var local = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.FromHours(2));

        var cursor = CommunityLogCursor.Encode(new CommunityLogPosition(local, 5));

        Assert.True(CommunityLogCursor.TryDecode(cursor, out var decoded));
        Assert.Equal(TimeSpan.Zero, decoded.CreatedAt.Offset);
        Assert.Equal(local.UtcDateTime, decoded.CreatedAt.UtcDateTime);
    }

    [Fact]
    public void Cursor_is_url_safe_text()
    {
        var cursor = CommunityLogCursor.Encode(new CommunityLogPosition(DateTimeOffset.UnixEpoch, long.MaxValue));

        Assert.Matches("^[A-Za-z0-9_-]+$", cursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a cursor!")]
    [InlineData("%%%%")]
    [InlineData("AAAA")]
    public void Garbage_is_rejected(string? cursor)
    {
        Assert.False(CommunityLogCursor.TryDecode(cursor, out _));
    }

    [Theory]
    [InlineData("v2.1.1")]
    [InlineData("v1.1")]
    [InlineData("v1.1.1.1")]
    [InlineData("v1.abc.1")]
    [InlineData("v1.-5.1")]
    [InlineData("v1.1.0")]
    [InlineData("v1.1.-3")]
    [InlineData("v1.99999999999999999999999.1")]
    [InlineData("v1.3155378976000000000.1")]
    [InlineData("v1.1.1 ")]
    public void Well_encoded_but_wrong_payload_is_rejected(string payload)
    {
        var cursor = Base64Url.EncodeToString(Encoding.ASCII.GetBytes(payload));

        Assert.False(CommunityLogCursor.TryDecode(cursor, out _));
    }

    [Fact]
    public void Standard_base64_with_padding_or_plus_is_rejected()
    {
        Assert.False(CommunityLogCursor.TryDecode("djEuMS4x==", out _));
        Assert.False(CommunityLogCursor.TryDecode("dj+uMS4x", out _));
    }

    [Fact]
    public void Oversized_cursor_is_rejected()
    {
        Assert.False(CommunityLogCursor.TryDecode(new string('A', 4096), out _));
    }
}
