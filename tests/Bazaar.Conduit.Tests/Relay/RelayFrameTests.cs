using Bazaar.Conduit.Relay;

namespace Bazaar.Conduit.Tests.Relay;

public sealed class RelayFrameTests
{
    [Fact]
    public void Register_frame_round_trips_through_the_codec()
    {
        var frame = RelayFrame.Register("room-1");

        var bytes = RelayFrameCodec.Encode(frame);

        Assert.Equal(frame, RelayFrameCodec.Decode(bytes));
    }

    [Fact]
    public void Register_frame_encodes_type_and_room_only()
    {
        var bytes = RelayFrameCodec.Encode(RelayFrame.Register("room-1"));

        Assert.Equal("{\"type\":\"register\",\"room\":\"room-1\"}", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Frame_without_room_omits_the_property()
    {
        var bytes = RelayFrameCodec.Encode(new RelayFrame("ping"));

        Assert.Equal("{\"type\":\"ping\"}", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(new RelayFrame("ping"), RelayFrameCodec.Decode(bytes));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"\"}")]
    [InlineData("[1,2,3]")]
    public void Malformed_or_untyped_bytes_decode_to_null(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        Assert.Null(RelayFrameCodec.Decode(bytes));
    }
}
