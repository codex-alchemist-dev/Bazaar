namespace Bazaar.Guest.Pointer.Tests;

// The expected bytes are written out by hand for a little-endian host (Wayland words are host-endian); the tool targets x64 and
// arm64 guests, both little-endian.
public class WaylandEncoderTests
{
    private static string Hex(Action<WaylandEncoder> build)
    {
        var e = new WaylandEncoder();
        build(e);
        return Convert.ToHexString(e.ToArray());
    }

    [Fact]
    public void Get_registry_is_display_opcode_1_with_the_new_id()
    {
        Assert.Equal("01000000" + "01000C00" + "02000000", Hex(e => e.GetRegistry(2)));
    }

    [Fact]
    public void Sync_is_display_opcode_0_with_the_callback_id()
    {
        Assert.Equal("01000000" + "00000C00" + "03000000", Hex(e => e.Sync(3)));
    }

    [Fact]
    public void Bind_carries_name_padded_interface_version_and_new_id()
    {
        // "zwlr_virtual_pointer_manager_v1" is 31 bytes, 32 with the NUL, which needs no padding.
        string expected =
            "02000000" + "00003800" + "07000000" + "20000000"
            + Convert.ToHexString("zwlr_virtual_pointer_manager_v1"u8) + "00"
            + "01000000" + "04000000";
        Assert.Equal(expected, Hex(e => e.Bind(2, 7, "zwlr_virtual_pointer_manager_v1", 1, 4)));
    }

    [Fact]
    public void Bind_pads_a_string_to_four_bytes_with_nuls()
    {
        // "wl_seat" is 7 bytes, 8 with the NUL: no padding. "ab" is 3 with the NUL: one pad byte.
        string expected = "02000000" + "00001C00" + "01000000" + "03000000" + "61620000" + "01000000" + "04000000";
        Assert.Equal(expected, Hex(e => e.Bind(2, 1, "ab", 1, 4)));
    }

    [Fact]
    public void Create_virtual_pointer_passes_a_null_seat_then_the_new_id()
    {
        Assert.Equal("04000000" + "00001000" + "00000000" + "05000000", Hex(e => e.CreateVirtualPointer(4, 5)));
    }

    [Fact]
    public void Motion_absolute_carries_time_position_and_extent()
    {
        Assert.Equal(
            "05000000" + "01001C00" + "34120000" + "64000000" + "C8000000" + "00050000" + "D0020000",
            Hex(e => e.MotionAbsolute(5, 0x1234, 100, 200, 1280, 720)));
    }

    [Fact]
    public void Button_carries_time_code_and_state()
    {
        Assert.Equal("05000000" + "02001400" + "01000000" + "10010000" + "01000000", Hex(e => e.Button(5, 1, 0x110, pressed: true)));
        Assert.Equal("05000000" + "02001400" + "02000000" + "11010000" + "00000000", Hex(e => e.Button(5, 2, 0x111, pressed: false)));
    }

    [Fact]
    public void Frame_and_destroy_have_no_arguments()
    {
        Assert.Equal("05000000" + "04000800", Hex(e => e.Frame(5)));
        Assert.Equal("05000000" + "08000800", Hex(e => e.DestroyPointer(5)));
    }

    [Fact]
    public void Axis_values_are_wl_fixed_24_8()
    {
        Assert.Equal("05000000" + "03001400" + "09000000" + "00000000" + "000F0000", Hex(e => e.Axis(5, 9, 0, 15)));
        Assert.Equal("05000000" + "03001400" + "09000000" + "01000000" + "00F1FFFF", Hex(e => e.Axis(5, 9, 1, -15)));
        Assert.Equal(3840, WaylandEncoder.ToFixed(15));
    }

    [Fact]
    public void Axis_source_and_axis_discrete()
    {
        Assert.Equal("05000000" + "05000C00" + "00000000", Hex(e => e.AxisSource(5, 0)));
        Assert.Equal(
            "05000000" + "07001800" + "09000000" + "00000000" + "000F0000" + "01000000",
            Hex(e => e.AxisDiscrete(5, 9, 0, 15, 1)));
        Assert.Equal(
            "05000000" + "07001800" + "09000000" + "00000000" + "00F1FFFF" + "FFFFFFFF",
            Hex(e => e.AxisDiscrete(5, 9, 0, -15, -1)));
    }

    [Fact]
    public void Take_returns_the_bytes_and_starts_over()
    {
        var e = new WaylandEncoder();
        e.Frame(5);
        Assert.Equal(8, e.Take().Length);
        Assert.Empty(e.ToArray());
    }
}

public class WaylandDecoderTests
{
    private static byte[] Message(uint id, ushort opcode, params uint[] words)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(id));
        bytes.AddRange(BitConverter.GetBytes((uint)((8 + words.Length * 4) << 16) | opcode));
        foreach (uint w in words)
            bytes.AddRange(BitConverter.GetBytes(w));
        return [.. bytes];
    }

    [Fact]
    public void Events_split_across_reads_are_joined()
    {
        byte[] data = Message(3, 0, 7);
        var d = new WaylandDecoder();
        d.Feed(data.AsSpan(0, 5));
        Assert.False(d.TryRead(out _));
        d.Feed(data.AsSpan(5, 4));
        Assert.False(d.TryRead(out _));
        d.Feed(data.AsSpan(9));
        Assert.True(d.TryRead(out WaylandEvent e));
        Assert.Equal((3u, (ushort)0, 7u), (e.ObjectId, e.Opcode, e.Word(0)));
        Assert.False(d.TryRead(out _));
    }

    [Fact]
    public void Two_events_in_one_read_come_out_in_order()
    {
        var d = new WaylandDecoder();
        d.Feed([.. Message(1, 1, 9), .. Message(3, 0, 0)]);
        Assert.True(d.TryRead(out WaylandEvent a));
        Assert.True(d.TryRead(out WaylandEvent b));
        Assert.Equal((1u, 3u), (a.ObjectId, b.ObjectId));
    }

    [Fact]
    public void A_message_smaller_than_its_header_is_rejected()
    {
        var d = new WaylandDecoder();
        d.Feed([1, 0, 0, 0, 0, 0, 4, 0]);
        Assert.Throws<InvalidDataException>(() => d.TryRead(out _));
    }

    [Fact]
    public void Strings_are_read_with_their_padding()
    {
        // length 4 ("abc" and NUL), then the next word begins right after.
        byte[] payload = [4, 0, 0, 0, (byte)'a', (byte)'b', (byte)'c', 0, 9, 0, 0, 0];
        Assert.Equal("abc", WaylandDecoder.ReadString(payload, 0, out int next));
        Assert.Equal(8, next);
    }

    [Fact]
    public void A_string_longer_than_its_payload_is_rejected()
    {
        byte[] payload = [200, 0, 0, 0, 1, 2, 3, 4];
        Assert.Throws<InvalidDataException>(() => WaylandDecoder.ReadString(payload, 0, out _));
    }
}
