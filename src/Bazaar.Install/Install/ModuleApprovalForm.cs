using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Install;

/// <summary>
/// The exact bytes the owner signs for a permission approval (doc 48 section 1). Layout: one version prefix byte, then the
/// domain tag, the module id, the version and the publisher id, each UTF-8 and preceded by its byte length as a 4-byte big-endian integer. After
/// those come each permission as <c>permission:&lt;name&gt;</c>, in ordinal sort order, with the same length prefix. The tags
/// keep a permission from being read as a module id. The publisher id is part of the form, so an approval given to one
/// publisher's module is not an approval for a module of the same id from another publisher.
/// </summary>
public static class ModuleApprovalForm
{
    /// <summary>The format version prefix byte.</summary>
    public const byte FormatVersion = 0x02;

    /// <summary>The domain tag. It stops an approval signature from being valid for any other signed form.</summary>
    public const string DomainTag = "bazaar.module.permissions.approved";

    /// <summary>The canonical bytes for one approval. Sorts and de-duplicates the permissions itself.</summary>
    public static byte[] Build(string moduleId, string version, string publisherId, IEnumerable<string> permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherId);
        ArgumentNullException.ThrowIfNull(permissions);

        var fields = new List<string>
        {
            DomainTag,
            moduleId,
            version,
            publisherId,
        };
        foreach (var permission in permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            fields.Add("permission:" + permission);
        }

        var encoded = fields.Select(Encoding.UTF8.GetBytes).ToList();
        var length = 1 + encoded.Sum(bytes => sizeof(int) + bytes.Length);
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
