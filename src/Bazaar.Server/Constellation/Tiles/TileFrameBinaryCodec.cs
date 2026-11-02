using System.Text;
using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Binary encoding of a <see cref="TileFrame"/> for the tile endpoint. Tile payloads are the approved binary exception to
/// JSON (doc 19). The format is little-endian and starts with the magic bytes <c>BZT1</c>, so a later version gets a new magic.
/// <see cref="Decode"/> treats its input as untrusted: every count is checked against the bytes that remain before anything is
/// allocated.
/// </summary>
public static class TileFrameBinaryCodec
{
    private static readonly byte[] _magic = "BZT1"u8.ToArray();

    // The smallest possible encoded node is a 1-byte string length plus three floats; a cluster adds an int32 member count.
    private const int _minimumNodeBytes = 1 + 3 * sizeof(float);
    private const int _minimumClusterBytes = 1 + sizeof(int) + 3 * sizeof(float);

    /// <summary>Encodes a frame. The output round-trips through <see cref="Decode"/>.</summary>
    public static byte[] Encode(TileFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(_magic);
            writer.Write(frame.Dimensions);
            writer.Write((byte)frame.Tier);
            writer.Write(frame.ViewId);
            writer.Write(frame.GraphVersion);
            writer.Write(frame.LayoutSeq);
            writer.Write(frame.Key.Level);
            writer.Write(frame.Key.X);
            writer.Write(frame.Key.Y);
            writer.Write(frame.Key.Z);

            writer.Write(frame.NodeIds.Count);
            foreach (var id in frame.NodeIds)
                writer.Write(id);
            foreach (var value in frame.Xyz.Span)
                writer.Write(value);

            writer.Write(frame.Clusters.Count);
            foreach (var cluster in frame.Clusters)
            {
                writer.Write(cluster.ClusterId);
                writer.Write(cluster.MemberCount);
                writer.Write(cluster.X);
                writer.Write(cluster.Y);
                writer.Write(cluster.Z);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Decodes a payload made by <see cref="Encode"/>. Throws <see cref="InvalidDataException"/> for anything else.</summary>
    public static TileFrame Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var stream = new MemoryStream(payload, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(_magic.Length).SequenceEqual(_magic))
                throw new InvalidDataException("Not a tile frame, or an unknown format version.");

            byte dimensions = reader.ReadByte();
            byte tier = reader.ReadByte();
            if (!Enum.IsDefined((ZoomTier)tier))
                throw new InvalidDataException("Unknown zoom tier.");

            string viewId = reader.ReadString();
            long graphVersion = reader.ReadInt64();
            long layoutSeq = reader.ReadInt64();
            var key = new TileKey(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());

            int nodeCount = reader.ReadInt32();
            RequireRoom(nodeCount, _minimumNodeBytes, stream);
            var nodeIds = new string[nodeCount];
            for (var i = 0; i < nodeCount; i++)
                nodeIds[i] = reader.ReadString();
            var xyz = new float[nodeCount * 3];
            for (var i = 0; i < xyz.Length; i++)
                xyz[i] = reader.ReadSingle();

            int clusterCount = reader.ReadInt32();
            RequireRoom(clusterCount, _minimumClusterBytes, stream);
            var clusters = new ClusterGlyph[clusterCount];
            for (var i = 0; i < clusterCount; i++)
            {
                clusters[i] = new ClusterGlyph(
                    reader.ReadString(),
                    reader.ReadInt32(),
                    reader.ReadSingle(),
                    reader.ReadSingle(),
                    reader.ReadSingle());
            }

            if (stream.Position != stream.Length)
                throw new InvalidDataException("Trailing bytes after the tile frame.");

            return new TileFrame(
                viewId,
                graphVersion,
                layoutSeq,
                key,
                (ZoomTier)tier,
                dimensions,
                nodeIds,
                xyz,
                clusters);
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("The tile frame is truncated or malformed.", e);
        }
    }

    private static void RequireRoom(int count, int minimumBytesEach, Stream stream)
    {
        long remaining = stream.Length - stream.Position;
        if (count < 0 || count * (long)minimumBytesEach > remaining)
            throw new InvalidDataException("A count in the tile frame does not fit in the payload.");
    }
}
