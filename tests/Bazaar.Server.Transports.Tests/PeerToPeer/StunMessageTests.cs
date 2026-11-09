using System.Buffers.Binary;
using System.Net;
using Bazaar.Server.Transports.PeerToPeer;

namespace Bazaar.Server.Transports.Tests.PeerToPeer;

public sealed class StunMessageTests
{
    private static readonly byte[] _transactionId = StunFixtures.Hex("00 01 02 03 04 05 06 07 08 09 0a 0b");

    [Fact]
    public void BindingRequest_HasHeaderCookieTypeZeroLengthAndTransactionId()
    {
        var request = StunMessage.CreateBindingRequest(_transactionId);

        Assert.Equal(StunMessage.HeaderLength, request.Packet.Length);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(request.Packet));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(request.Packet.AsSpan(2)));
        Assert.Equal(0x2112A442u, BinaryPrimitives.ReadUInt32BigEndian(request.Packet.AsSpan(4)));
        Assert.Equal(_transactionId, request.Packet.AsSpan(8, 12).ToArray());
        Assert.Equal(_transactionId, request.TransactionId);
    }

    [Fact]
    public void BindingRequest_RandomTransactionIdIsTwelveBytesAndDiffersBetweenRequests()
    {
        var first = StunMessage.CreateBindingRequest();
        var second = StunMessage.CreateBindingRequest();

        Assert.Equal(12, first.TransactionId.Length);
        Assert.Equal(first.TransactionId, first.Packet.AsSpan(8, 12).ToArray());
        Assert.NotEqual(first.TransactionId, second.TransactionId);
    }

    [Fact]
    public void BindingRequest_RejectsTransactionIdOfWrongLength()
    {
        Assert.Throws<ArgumentException>(() => StunMessage.CreateBindingRequest(new byte[11]));
    }

    [Fact]
    public void ReadMappedAddress_Rfc5769Ipv4Sample_DecodesXorMappedAddress()
    {
        var response = StunFixtures.Hex(StunFixtures.Rfc5769Ipv4Response);

        var mapped = StunMessage.ReadMappedAddress(response, StunFixtures.Rfc5769TransactionId);

        Assert.NotNull(mapped);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), mapped.Address);
        Assert.Equal(32853, mapped.Port);
    }

    [Fact]
    public void ReadMappedAddress_Rfc5769Ipv6Sample_DecodesXorMappedAddress()
    {
        var response = StunFixtures.Hex(StunFixtures.Rfc5769Ipv6Response);

        var mapped = StunMessage.ReadMappedAddress(response, StunFixtures.Rfc5769TransactionId);

        Assert.NotNull(mapped);
        Assert.Equal(IPAddress.Parse("2001:db8:1234:5678:11:2233:4455:6677"), mapped.Address);
        Assert.Equal(32853, mapped.Port);
    }

    [Fact]
    public void ReadMappedAddress_SelfBuiltIpv6Fixture_DecodesWithIndependentlyEncodedXor()
    {
        // Encoded by StunFixtures.EncodeAddress (independent of the decoder): XOR with cookie then transaction id.
        var expected = new IPEndPoint(IPAddress.Parse("2001:db8::7:8"), 49152);
        var response = StunFixtures.SuccessResponse(_transactionId, expected);

        var mapped = StunMessage.ReadMappedAddress(response, _transactionId);

        Assert.Equal(expected, mapped);
    }

    [Fact]
    public void ReadMappedAddress_SelfBuiltIpv6Fixture_XorKeyIncludesTransactionId()
    {
        // The address is XORed with a different transaction id than the header carries, so it must not decode back.
        var expected = new IPEndPoint(IPAddress.Parse("2001:db8::7:8"), 49152);
        var otherId = StunFixtures.Hex("ff ff ff ff ff ff ff ff ff ff ff ff");
        var response = StunFixtures.Message(_transactionId, [(0x0020, StunFixtures.EncodeAddress(expected, otherId, xor: true))]);

        Assert.NotEqual(expected, StunMessage.ReadMappedAddress(response, _transactionId));
    }

    [Fact]
    public void ReadMappedAddress_FallsBackToMappedAddressAttribute()
    {
        var expected = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 3478);
        var response = StunFixtures.PlainMappedResponse(_transactionId, expected);

        Assert.Equal(expected, StunMessage.ReadMappedAddress(response, _transactionId));
    }

    [Fact]
    public void ReadMappedAddress_PrefersXorMappedAddressOverMappedAddress()
    {
        var plain = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 3478);
        var xor = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000);
        var response = StunFixtures.Message(_transactionId,
        [
            (0x0001, StunFixtures.EncodeAddress(plain, _transactionId, xor: false)),
            (0x0020, StunFixtures.EncodeAddress(xor, _transactionId, xor: true)),
        ]);

        Assert.Equal(xor, StunMessage.ReadMappedAddress(response, _transactionId));
    }

    [Fact]
    public void ReadMappedAddress_SkipsUnknownAttributesWithPadding()
    {
        var expected = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000);
        var response = StunFixtures.Message(_transactionId,
        [
            (0x8023, new byte[] { 1, 2, 3, 4, 5 }),
            (0x0020, StunFixtures.EncodeAddress(expected, _transactionId, xor: true)),
        ]);

        Assert.Equal(expected, StunMessage.ReadMappedAddress(response, _transactionId));
    }

    [Fact]
    public void ReadMappedAddress_IgnoresResponseWithDifferentTransactionId()
    {
        var response = StunFixtures.Hex(StunFixtures.Rfc5769Ipv4Response);
        var otherId = StunFixtures.Hex("b7 e7 a7 01 bc 34 d6 86 fa 87 df af");

        Assert.Null(StunMessage.ReadMappedAddress(response, otherId));
    }

    [Fact]
    public void ReadMappedAddress_IgnoresErrorResponse()
    {
        var response = StunFixtures.Message(_transactionId, [(0x0020, StunFixtures.EncodeAddress(new IPEndPoint(IPAddress.Loopback, 1), _transactionId, true))], messageType: 0x0111);

        Assert.Null(StunMessage.ReadMappedAddress(response, _transactionId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("01 01 00 00 21 12 a4")]
    [InlineData("01 01 00 00 21 12 a4 00 00 00 00 00 00 00 00 00 00 00 00 00")]
    [InlineData("00 01 00 00 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae")]
    [InlineData("01 01 00 04 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae")]
    [InlineData("01 01 00 03 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae 00 00 00")]
    [InlineData("01 01 00 08 21 12 a4 43 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae 00 00 00 00 00 00 00")]
    [InlineData("01 01 00 0c 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae 00 20 00 08 00 01 a1")]
    [InlineData("01 01 00 0c 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae 00 20 00 40 00 01 a1 47")]
    [InlineData("01 01 00 0c 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae 00 20 00 08 00 03 a1 47 e1 12 a6 43")]
    [InlineData("01 01 00 0c 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae 00 20 00 08 00 02 a1 47 e1 12 a6 43")]
    public void ReadMappedAddress_MalformedInput_ReturnsNullWithoutThrowing(string hex)
    {
        var bytes = StunFixtures.Hex(hex);

        Assert.Null(StunMessage.ReadMappedAddress(bytes, StunFixtures.Rfc5769TransactionId));
    }

    [Fact]
    public void ReadMappedAddress_TruncatedRfcResponse_ReturnsNullWithoutThrowing()
    {
        var full = StunFixtures.Hex(StunFixtures.Rfc5769Ipv4Response);

        for (var length = 0; length < full.Length; length++)
        {
            var truncated = full.AsSpan(0, length);
            Assert.Null(StunMessage.ReadMappedAddress(truncated, StunFixtures.Rfc5769TransactionId));
        }
    }

    [Fact]
    public void ReadMappedAddress_RandomBytes_NeverThrows()
    {
        var random = new Random(5769);
        var buffer = new byte[96];
        for (var round = 0; round < 5000; round++)
        {
            random.NextBytes(buffer);
            var length = random.Next(0, buffer.Length + 1);

            var result = StunMessage.ReadMappedAddress(buffer.AsSpan(0, length), StunFixtures.Rfc5769TransactionId);

            Assert.True(result is null || result.Port > 0);
        }
    }
}
