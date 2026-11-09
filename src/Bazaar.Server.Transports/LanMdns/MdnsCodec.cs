using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bazaar.Server.Transports.LanMdns;

/// <summary>
/// DNS wire-format codec for the mDNS subset Bazaar uses: header, questions and PTR, SRV, TXT and A records.
/// Encoding writes names uncompressed. Decoding follows compression pointers and rejects malformed input.
/// </summary>
public static class MdnsCodec
{
    private const int _headerLength = 12;
    private const int _maxNameLength = 255;
    private const int _maxLabelLength = 63;
    private const int _maxPointerJumps = 64;
    private const ushort _flagResponse = 0x8000;
    private const ushort _flagAuthoritative = 0x0400;
    private const byte _pointerMask = 0xC0;

    /// <summary>Encodes a message. Throws <see cref="ArgumentException"/> when a name or string cannot be encoded.</summary>
    public static byte[] Encode(MdnsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var writer = new Writer();
        writer.WriteU16(message.Id);
        ushort flags = 0;
        if (message.IsResponse)
        {
            flags |= _flagResponse;
        }

        if (message.Authoritative)
        {
            flags |= _flagAuthoritative;
        }

        writer.WriteU16(flags);
        writer.WriteU16(checked((ushort)message.Questions.Count));
        writer.WriteU16(checked((ushort)message.Answers.Count));
        writer.WriteU16(checked((ushort)message.Authorities.Count));
        writer.WriteU16(checked((ushort)message.Additionals.Count));

        foreach (var question in message.Questions)
        {
            writer.WriteName(question.Name);
            writer.WriteU16(question.Type);
            writer.WriteU16((ushort)(question.Class | (question.UnicastResponse ? MdnsTypes.TopBit : 0)));
        }

        foreach (var record in message.Answers.Concat(message.Authorities).Concat(message.Additionals))
        {
            WriteRecord(writer, record);
        }

        return writer.ToArray();
    }

    /// <summary>Decodes a packet. Throws <see cref="FormatException"/> when the packet is truncated or malformed.</summary>
    public static MdnsMessage Decode(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length < _headerLength)
        {
            throw new FormatException("Packet is shorter than the DNS header.");
        }

        var reader = new Reader(packet, _headerLength);
        var id = U16At(packet, 0);
        var flags = U16At(packet, 2);
        var questionCount = U16At(packet, 4);
        var answerCount = U16At(packet, 6);
        var authorityCount = U16At(packet, 8);
        var additionalCount = U16At(packet, 10);

        // Each question needs at least 5 bytes and each record at least 11, so larger counts cannot be honest.
        if (questionCount * 5 + (answerCount + authorityCount + additionalCount) * 11 > packet.Length)
        {
            throw new FormatException("Record counts exceed the packet length.");
        }

        var questions = new List<MdnsQuestion>(questionCount);
        for (var i = 0; i < questionCount; i++)
        {
            var name = reader.ReadName();
            var type = reader.ReadU16();
            var rawClass = reader.ReadU16();
            questions.Add(new MdnsQuestion(name, type, (ushort)(rawClass & ~MdnsTypes.TopBit), (rawClass & MdnsTypes.TopBit) != 0));
        }

        var answers = ReadRecords(reader, answerCount);
        var authorities = ReadRecords(reader, authorityCount);
        var additionals = ReadRecords(reader, additionalCount);

        return new MdnsMessage(
            id,
            (flags & _flagResponse) != 0,
            (flags & _flagAuthoritative) != 0,
            questions,
            answers,
            authorities,
            additionals);
    }

    private static List<MdnsResourceRecord> ReadRecords(Reader reader, int count)
    {
        var records = new List<MdnsResourceRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadName();
            var type = reader.ReadU16();
            var rawClass = reader.ReadU16();
            var ttl = reader.ReadU32();
            var length = reader.ReadU16();
            var end = reader.Position + length;
            if (end > reader.Length)
            {
                throw new FormatException("Record data runs past the packet.");
            }

            var data = ReadRecordData(reader, type, end);
            reader.Seek(end);
            records.Add(new MdnsResourceRecord(name, (ushort)(rawClass & ~MdnsTypes.TopBit), (rawClass & MdnsTypes.TopBit) != 0, ttl, data));
        }

        return records;
    }

    private static MdnsRecordData ReadRecordData(Reader reader, ushort type, int end)
    {
        switch (type)
        {
            case MdnsTypes.PtrRecordType:
                return new PtrRecordData(reader.ReadName());

            case MdnsTypes.Srv:
                var priority = reader.ReadU16();
                var weight = reader.ReadU16();
                var port = reader.ReadU16();
                return new SrvRecordData(priority, weight, port, reader.ReadName());

            case MdnsTypes.Txt:
                var strings = new List<string>();
                while (reader.Position < end)
                {
                    var length = reader.ReadByte();
                    if (reader.Position + length > end)
                    {
                        throw new FormatException("TXT string runs past its record.");
                    }

                    strings.Add(Encoding.UTF8.GetString(reader.Slice(length)));
                }

                return new TxtRecordData(strings);

            case MdnsTypes.A:
                if (end - reader.Position != 4)
                {
                    throw new FormatException("A record must carry exactly four bytes.");
                }

                return new ARecordData(new IPAddress(reader.Slice(4)));

            default:
                return new RawRecordData(type, reader.Slice(end - reader.Position));
        }
    }

    private static void WriteRecord(Writer writer, MdnsResourceRecord record)
    {
        writer.WriteName(record.Name);
        writer.WriteU16(record.Type);
        writer.WriteU16((ushort)(record.Class | (record.CacheFlush ? MdnsTypes.TopBit : 0)));
        writer.WriteU32(record.Ttl);

        var lengthAt = writer.Length;
        writer.WriteU16(0);
        var dataStart = writer.Length;

        switch (record.Data)
        {
            case PtrRecordData ptr:
                writer.WriteName(ptr.Target);
                break;

            case SrvRecordData srv:
                writer.WriteU16(srv.Priority);
                writer.WriteU16(srv.Weight);
                writer.WriteU16(srv.Port);
                writer.WriteName(srv.Target);
                break;

            case TxtRecordData txt:
                if (txt.Strings.Count == 0)
                {
                    writer.WriteByte(0);
                }

                foreach (var text in txt.Strings)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    if (bytes.Length > byte.MaxValue)
                    {
                        throw new ArgumentException("TXT strings are limited to 255 bytes.", nameof(record));
                    }

                    writer.WriteByte((byte)bytes.Length);
                    writer.WriteBytes(bytes);
                }

                break;

            case ARecordData a:
                writer.WriteBytes(a.Address.GetAddressBytes());
                break;

            case RawRecordData raw:
                writer.WriteBytes(raw.Bytes);
                break;

            default:
                throw new ArgumentException($"Unsupported record data {record.Data.GetType().Name}.", nameof(record));
        }

        writer.PatchU16(lengthAt, checked((ushort)(writer.Length - dataStart)));
    }

    private static ushort U16At(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private sealed class Writer
    {
        private readonly List<byte> _bytes = new(512);

        public int Length => _bytes.Count;

        public void WriteByte(byte value) => _bytes.Add(value);

        public void WriteU16(ushort value)
        {
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }

        public void WriteU32(uint value)
        {
            WriteU16((ushort)(value >> 16));
            WriteU16((ushort)value);
        }

        public void WriteBytes(byte[] bytes) => _bytes.AddRange(bytes);

        public void PatchU16(int offset, ushort value)
        {
            _bytes[offset] = (byte)(value >> 8);
            _bytes[offset + 1] = (byte)value;
        }

        public void WriteName(string name)
        {
            ArgumentNullException.ThrowIfNull(name);

            var trimmed = name.TrimEnd('.');
            var total = 1;
            if (trimmed.Length > 0)
            {
                foreach (var label in trimmed.Split('.'))
                {
                    var bytes = Encoding.UTF8.GetBytes(label);
                    if (bytes.Length is 0 or > _maxLabelLength)
                    {
                        throw new ArgumentException($"Label '{label}' must be 1 to {_maxLabelLength} bytes.", nameof(name));
                    }

                    total += bytes.Length + 1;
                    _bytes.Add((byte)bytes.Length);
                    _bytes.AddRange(bytes);
                }
            }

            if (total > _maxNameLength)
            {
                throw new ArgumentException("Name exceeds 255 bytes.", nameof(name));
            }

            _bytes.Add(0);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private sealed class Reader(byte[] data, int position)
    {
        public int Position { get; private set; } = position;

        public int Length => data.Length;

        public void Seek(int position) => Position = position;

        public byte ReadByte()
        {
            Require(1);
            return data[Position++];
        }

        public ushort ReadU16()
        {
            Require(2);
            var value = U16At(data, Position);
            Position += 2;
            return value;
        }

        public uint ReadU32()
        {
            var high = ReadU16();
            var low = ReadU16();
            return ((uint)high << 16) | low;
        }

        public byte[] Slice(int length)
        {
            Require(length);
            var bytes = new byte[length];
            Array.Copy(data, Position, bytes, 0, length);
            Position += length;
            return bytes;
        }

        /// <summary>Reads a name at the current position, following compression pointers. The position moves past the name's first encoding.</summary>
        public string ReadName()
        {
            var labels = new List<string>();
            var cursor = Position;
            var end = -1;
            var jumps = 0;
            var total = 0;

            while (true)
            {
                if (cursor >= data.Length)
                {
                    throw new FormatException("Name runs past the packet.");
                }

                var length = data[cursor];
                if ((length & _pointerMask) == _pointerMask)
                {
                    if (cursor + 1 >= data.Length)
                    {
                        throw new FormatException("Compression pointer is truncated.");
                    }

                    if (++jumps > _maxPointerJumps)
                    {
                        throw new FormatException("Compression pointers loop.");
                    }

                    end = end < 0 ? cursor + 2 : end;
                    cursor = ((length & ~_pointerMask) << 8) | data[cursor + 1];
                    continue;
                }

                if ((length & _pointerMask) != 0)
                {
                    throw new FormatException("Reserved label type.");
                }

                cursor++;
                if (length == 0)
                {
                    end = end < 0 ? cursor : end;
                    break;
                }

                if (cursor + length > data.Length)
                {
                    throw new FormatException("Label runs past the packet.");
                }

                total += length + 1;
                if (total > _maxNameLength)
                {
                    throw new FormatException("Name exceeds 255 bytes.");
                }

                labels.Add(Encoding.UTF8.GetString(data, cursor, length));
                cursor += length;
            }

            Position = end;
            return string.Join('.', labels);
        }

        private void Require(int count)
        {
            if (Position + count > data.Length)
            {
                throw new FormatException("Packet is truncated.");
            }
        }
    }
}
