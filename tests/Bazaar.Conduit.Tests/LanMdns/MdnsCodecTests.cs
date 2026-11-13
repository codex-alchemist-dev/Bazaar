using System.Net;
using System.Text;
using Bazaar.Conduit.LanMdns;

namespace Bazaar.Conduit.Tests.LanMdns;

public sealed class MdnsCodecTests
{
    private const string _instance = "bazaar._bazaar._tcp.local";

    [Fact]
    public void Ptr_srv_txt_and_a_response_round_trips()
    {
        var message = new MdnsMessage(
            0,
            IsResponse: true,
            Authoritative: true,
            Questions: [],
            Answers:
            [
                new MdnsResourceRecord("_bazaar._tcp.local", MdnsTypes.ClassIn, false, 4500, new PtrRecordData(_instance)),
            ],
            Authorities: [],
            Additionals:
            [
                new MdnsResourceRecord(_instance, MdnsTypes.ClassIn, true, 120, new SrvRecordData(0, 0, 7443, "bazaar.local")),
                new MdnsResourceRecord(_instance, MdnsTypes.ClassIn, true, 120, new TxtRecordData(["keyhash=AAAAAAAAAAAAAAAA-fixture", "ver=1.2.3"])),
                new MdnsResourceRecord("bazaar.local", MdnsTypes.ClassIn, true, 120, new ARecordData(IPAddress.Parse("192.168.1.20"))),
            ]);

        var decoded = MdnsCodec.Decode(MdnsCodec.Encode(message));

        Assert.True(decoded.IsResponse);
        Assert.True(decoded.Authoritative);
        Assert.Empty(decoded.Questions);

        var ptr = Assert.Single(decoded.Answers);
        Assert.Equal("_bazaar._tcp.local", ptr.Name);
        Assert.Equal(MdnsTypes.PtrRecordType, ptr.Type);
        Assert.False(ptr.CacheFlush);
        Assert.Equal(4500u, ptr.Ttl);
        Assert.Equal(_instance, Assert.IsType<PtrRecordData>(ptr.Data).Target);

        Assert.Equal(3, decoded.Additionals.Count(r => r.CacheFlush));
        var srv = Assert.IsType<SrvRecordData>(decoded.Additionals[0].Data);
        Assert.Equal((ushort)7443, srv.Port);
        Assert.Equal("bazaar.local", srv.Target);
        Assert.True(decoded.Additionals[0].CacheFlush);

        var txt = Assert.IsType<TxtRecordData>(decoded.Additionals[1].Data);
        Assert.Equal(["keyhash=AAAAAAAAAAAAAAAA-fixture", "ver=1.2.3"], txt.Strings);

        var a = Assert.IsType<ARecordData>(decoded.Additionals[2].Data);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), a.Address);
    }

    [Fact]
    public void Query_question_round_trips_with_the_unicast_bit()
    {
        var message = new MdnsMessage(
            7,
            IsResponse: false,
            Authoritative: false,
            Questions: [new MdnsQuestion("_bazaar._tcp.local", MdnsTypes.PtrRecordType, MdnsTypes.ClassIn, UnicastResponse: true)],
            Answers: [],
            Authorities: [],
            Additionals: []);

        var decoded = MdnsCodec.Decode(MdnsCodec.Encode(message));

        Assert.Equal((ushort)7, decoded.Id);
        Assert.False(decoded.IsResponse);
        var question = Assert.Single(decoded.Questions);
        Assert.Equal("_bazaar._tcp.local", question.Name);
        Assert.Equal(MdnsTypes.PtrRecordType, question.Type);
        Assert.Equal(MdnsTypes.ClassIn, question.Class);
        Assert.True(question.UnicastResponse);
    }

    [Fact]
    public void Compressed_names_decode_to_their_full_form()
    {
        var packet = CompressedResponse();

        var decoded = MdnsCodec.Decode(packet);

        Assert.Equal(2, decoded.Answers.Count);
        Assert.Equal("_bazaar._tcp.local", decoded.Answers[0].Name);
        Assert.Equal("bazaar._bazaar._tcp.local", Assert.IsType<PtrRecordData>(decoded.Answers[0].Data).Target);
        Assert.Equal("host._bazaar._tcp.local", decoded.Answers[1].Name);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), Assert.IsType<ARecordData>(decoded.Answers[1].Data).Address);
    }

    [Fact]
    public void A_pointer_loop_is_rejected()
    {
        // Header with one question whose name is a pointer to itself.
        var packet = new byte[] { 0, 0, 0x00, 0x00, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C, 0, 12, 0, 1 };

        Assert.Throws<FormatException>(() => MdnsCodec.Decode(packet));
    }

    [Fact]
    public void Truncated_packet_is_rejected()
    {
        var packet = CompressedResponse();

        Assert.Throws<FormatException>(() => MdnsCodec.Decode(packet[..(packet.Length - 3)]));
        Assert.Throws<FormatException>(() => MdnsCodec.Decode([1, 2, 3]));
    }

    /// <summary>A response with two answers. The first owner is a pointer to the service name, the PTR target reuses it, and the second owner is a new label followed by a pointer.</summary>
    private static byte[] CompressedResponse()
    {
        var packet = new List<byte>();
        void U16(int value)
        {
            packet.Add((byte)(value >> 8));
            packet.Add((byte)value);
        }

        void U32(uint value)
        {
            U16((int)(value >> 16));
            U16((int)(value & 0xFFFF));
        }

        void Label(string text)
        {
            packet.Add((byte)text.Length);
            packet.AddRange(Encoding.ASCII.GetBytes(text));
        }

        U16(0);
        U16(0x8400);
        U16(1);
        U16(2);
        U16(0);
        U16(0);

        // Offset 12: question for _bazaar._tcp.local, the name later records point at.
        Label("_bazaar");
        Label("_tcp");
        Label("local");
        packet.Add(0);
        U16(MdnsTypes.PtrRecordType);
        U16(MdnsTypes.ClassIn);

        // PTR owned by offset 12; rdata is "bazaar" followed by a pointer to offset 12.
        U16(0xC00C);
        U16(MdnsTypes.PtrRecordType);
        U16(MdnsTypes.ClassIn);
        U32(4500);
        var lengthAt = packet.Count;
        U16(0);
        var start = packet.Count;
        Label("bazaar");
        U16(0xC00C);
        var length = packet.Count - start;
        packet[lengthAt] = (byte)(length >> 8);
        packet[lengthAt + 1] = (byte)length;

        // A record owned by "host" plus a pointer to offset 12.
        Label("host");
        U16(0xC00C);
        U16(MdnsTypes.A);
        U16(MdnsTypes.ClassIn);
        U32(120);
        U16(4);
        packet.AddRange([192, 168, 1, 20]);

        return packet.ToArray();
    }
}
