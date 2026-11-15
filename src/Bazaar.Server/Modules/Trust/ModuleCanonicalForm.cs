using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// The exact bytes a publisher signs for a module package.
/// </summary>
/// <remarks>
/// Implementation choice pending the signing spike in doc 35: doc 48 leaves the canonical layout open. Layout: one version
/// prefix byte (0x01), then the module id, the version, the content digest and each declared permission in ordinal sort
/// order, every field UTF-8 encoded and preceded by its byte length as a 4-byte big-endian integer. The field count is
/// implied by the length prefixes, so no field can be read as part of another. The signer key id is not signed, because
/// the key is looked up by that id and must verify under it. Changing this layout needs a new prefix byte.
/// </remarks>
public static class ModuleCanonicalForm
{
    /// <summary>The format version prefix byte.</summary>
    public const byte FormatVersion = 0x01;

    /// <summary>The canonical bytes for the package. Null-checks the package; does not validate its fields.</summary>
    public static byte[] Build(ModulePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var fields = new List<string>(capacity: 3 + package.DeclaredPermissions.Count)
        {
            package.ModuleId,
            package.Version,
            package.ContentDigestSha256Hex,
        };
        fields.AddRange(package.DeclaredPermissions.Order(StringComparer.Ordinal));

        var encoded = new List<byte[]>(fields.Count);
        var length = 1;
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            encoded.Add(bytes);
            length += sizeof(int) + bytes.Length;
        }

        var result = new byte[length];
        result[0] = FormatVersion;
        var offset = 1;
        foreach (var bytes in encoded)
        {
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, sizeof(int)), bytes.Length);
            offset += sizeof(int);
            bytes.CopyTo(result, offset);
            offset += bytes.Length;
        }

        return result;
    }
}
