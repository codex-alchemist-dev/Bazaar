using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Server.Install.Trust;

/// <summary>
/// The exact bytes a publisher signs for a module package.
/// </summary>
/// <remarks>
/// Format 0x02 (owner, 2026-11-16: host requirements are signed). Layout: one version prefix byte, then the module id, the
/// version and the content digest, each UTF-8 and preceded by its byte length as a 4-byte big-endian integer. After those come
/// tagged fields, in this order: each declared permission as <c>permission:&lt;name&gt;</c> in ordinal sort order, then the host
/// requirements as <c>host.schema:&lt;n&gt;</c>, <c>host.os:&lt;name&gt;</c>, <c>host.arch:&lt;name&gt;</c> and
/// <c>host.feature:&lt;name&gt;</c>, each group sorted ordinal. The tags keep a permission from being read as a host value.
/// The signer key id is not signed, because the key is looked up by that id and must verify under it. Format 0x01 (no host
/// requirements) is retired; no module package has been published under it.
/// </remarks>
public static class ModuleCanonicalForm
{
    /// <summary>The format version prefix byte.</summary>
    public const byte FormatVersion = 0x02;

    /// <summary>The canonical bytes for the package. Null-checks the package; does not validate its fields.</summary>
    public static byte[] Build(ModulePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        var fields = new List<string>(capacity: 3 + package.DeclaredPermissions.Count + 1)
        {
            package.ModuleId,
            package.Version,
            package.ContentDigestSha256Hex,
        };
        foreach (var permission in package.DeclaredPermissions.Order(StringComparer.Ordinal))
        {
            fields.Add("permission:" + permission);
        }

        var host = package.Host;
        fields.Add("host.schema:" + (host?.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
        AddSorted(fields, "host.os:", host?.OperatingSystems);
        AddSorted(fields, "host.arch:", host?.Architectures);
        AddSorted(fields, "host.feature:", host?.RequiredFeatures);

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

    private static void AddSorted(List<string> fields, string tag, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            fields.Add(tag + value);
        }
    }
}
