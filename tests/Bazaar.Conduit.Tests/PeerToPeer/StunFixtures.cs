using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Bazaar.Conduit.PeerToPeer;

namespace Bazaar.Conduit.Tests.PeerToPeer;

/// <summary>Byte fixtures and independent encoders for the STUN tests. The encoders do not call the codec under test.</summary>
internal static class StunFixtures
{
    /// <summary>RFC 5769 section 2.2 transaction id.</summary>
    public static readonly byte[] Rfc5769TransactionId = Hex("b7e7a701bc34d686fa87dfae");

    /// <summary>RFC 5769 section 2.2: IPv4 Binding Success Response, verbatim (80 bytes, with MESSAGE-INTEGRITY and FINGERPRINT).</summary>
    public const string Rfc5769Ipv4Response =
        "01 01 00 3c 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae " +
        "80 22 00 0b 74 65 73 74 20 76 65 63 74 6f 72 20 " +
        "00 20 00 08 00 01 a1 47 e1 12 a6 43 " +
        "00 08 00 14 2b 91 f5 99 fd 9e 90 c3 8c 74 89 f9 2a f9 ba 53 f0 6b e7 d7 " +
        "80 28 00 04 c0 7d 4c 96";

    /// <summary>RFC 5769 section 2.3: IPv6 Binding Success Response, verbatim.</summary>
    public const string Rfc5769Ipv6Response =
        "01 01 00 48 21 12 a4 42 b7 e7 a7 01 bc 34 d6 86 fa 87 df ae " +
        "80 22 00 0b 74 65 73 74 20 76 65 63 74 6f 72 20 " +
        "00 20 00 14 00 02 a1 47 01 13 a9 fa a5 d3 f1 79 bc 25 f4 b5 be d2 b9 d9 " +
        "00 08 00 14 a3 82 95 4e 4b e6 7b f1 17 84 c9 7c 82 92 c2 75 bf e3 ed 41 " +
        "80 28 00 04 c8 fb 0b 4c";

    /// <summary>Parses hex text, ignoring spaces.</summary>
    public static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));

    /// <summary>Builds a Binding Success Response with an XOR-MAPPED-ADDRESS attribute for <paramref name="mapped"/>.</summary>
    public static byte[] SuccessResponse(ReadOnlySpan<byte> transactionId, IPEndPoint mapped)
    {
        var value = EncodeAddress(mapped, transactionId, xor: true);
        return Message(transactionId, [(0x0020, value)]);
    }

    /// <summary>Builds a Binding Success Response that carries a plain MAPPED-ADDRESS attribute.</summary>
    public static byte[] PlainMappedResponse(ReadOnlySpan<byte> transactionId, IPEndPoint mapped)
    {
        var value = EncodeAddress(mapped, transactionId, xor: false);
        return Message(transactionId, [(0x0001, value)]);
    }

    /// <summary>Builds a message from attribute (type, value) pairs. Each value is padded to four bytes.</summary>
    public static byte[] Message(ReadOnlySpan<byte> transactionId, IEnumerable<(ushort Type, byte[] Value)> attributes, ushort messageType = 0x0101)
    {
        var body = new List<byte>();
        Span<byte> header = stackalloc byte[4];
        foreach (var (type, value) in attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(header, type);
            BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)value.Length);
            body.AddRange(header.ToArray());
            body.AddRange(value);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        var message = new byte[20 + body.Count];
        BinaryPrimitives.WriteUInt16BigEndian(message, messageType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(2), (ushort)body.Count);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), 0x2112A442);
        transactionId.CopyTo(message.AsSpan(8));
        body.CopyTo(message, 20);
        return message;
    }

    /// <summary>XOR-MAPPED or MAPPED address attribute value, encoded here by hand per RFC 5389 section 15.2.</summary>
    public static byte[] EncodeAddress(IPEndPoint endpoint, ReadOnlySpan<byte> transactionId, bool xor)
    {
        var address = endpoint.Address.GetAddressBytes();
        var port = (ushort)endpoint.Port;
        var key = new byte[] { 0x21, 0x12, 0xA4, 0x42 }.Concat(transactionId.ToArray()).ToArray();

        var value = new List<byte> { 0, (byte)(address.Length == 4 ? 0x01 : 0x02) };
        if (xor)
        {
            port ^= 0x2112;
            for (var i = 0; i < address.Length; i++)
            {
                address[i] ^= key[i];
            }
        }

        value.Add((byte)(port >> 8));
        value.Add((byte)(port & 0xFF));
        value.AddRange(address);
        return value.ToArray();
    }

    /// <summary>A UDP STUN server on loopback. It answers each request with <see cref="Respond"/>.</summary>
    internal sealed class LoopbackStunServer : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public LoopbackStunServer(Func<byte[], byte[]?> respond)
        {
            Endpoint = (IPEndPoint)_socket.Client.LocalEndPoint!;
            _loop = Run(respond);
        }

        /// <summary>The bound endpoint on 127.0.0.1.</summary>
        public IPEndPoint Endpoint { get; }

        /// <summary>Requests received so far.</summary>
        public int RequestCount { get; private set; }

        private async Task Run(Func<byte[], byte[]?> respond)
        {
            try
            {
                while (true)
                {
                    var received = await _socket.ReceiveAsync(_stop.Token);
                    RequestCount++;
                    var reply = respond(received.Buffer);
                    if (reply is not null)
                    {
                        await _socket.SendAsync(reply, reply.Length, received.RemoteEndPoint);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed.
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _socket.Dispose();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // The loop ends with cancellation or a socket error after disposal.
            }

            _stop.Dispose();
        }
    }
}
