using System.IO;
using System.Text;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// Decodes the binary tile body of <c>POST /constellation/tiles</c> (<c>application/x-bazaar-tile</c>). The layout mirrors the
/// server's <c>TileFrameBinaryCodec</c>, which is the authority: little-endian, magic <c>BZT1</c>, then dimensions (byte), tier
/// (byte), view id, graph version, layout sequence, the tile key, the node ids and packed x,y,z floats, and the clusters.
/// Strings are length-prefixed UTF-8 (<see cref="BinaryReader.ReadString"/>). The input is untrusted, so every count is checked
/// against the bytes that remain before anything is allocated.
/// </summary>
public static class TileFrameDecoder
{
    private static readonly byte[] _magic = "BZT1"u8.ToArray();

    // The smallest possible encoded node is a 1-byte string length plus three floats; a cluster adds an int32 member count.
    private const int _minimumNodeBytes = 1 + (3 * sizeof(float));
    private const int _minimumClusterBytes = 1 + sizeof(int) + (3 * sizeof(float));

    /// <summary>Decodes one tile body. Throws <see cref="InvalidDataException"/> for anything malformed or from another format.</summary>
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
            if (!Enum.IsDefined((Bazaar.Contracts.Graph.ZoomTier)tier))
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

            return new TileFrame(viewId, graphVersion, layoutSeq, key, (Bazaar.Contracts.Graph.ZoomTier)tier, dimensions, nodeIds, xyz, clusters);
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
