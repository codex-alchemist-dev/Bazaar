using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// The template hash (doc 45 section 1.8). It identifies the exact content of one template version, and the apply row records it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scheme, version 1.</b> The hash is the lowercase hex SHA-256 of this byte stream, built in order:
/// </para>
/// <list type="number">
/// <item><description>The header text <c>bazaar-template-hash/v1</c> followed by a line feed (U+000A).</description></item>
/// <item><description>The text <c>template.json</c> and a line feed.</description></item>
/// <item><description>The canonical JSON of the normalized template model (the form written by <see cref="SetupPlanCanonicalizer"/>:
/// UTF-8, object keys sorted ordinally, no insignificant whitespace, defaults filled in, set-like lists sorted). The file's own
/// spelling, key order, whitespace and comments do not change it. Then a line feed.</description></item>
/// <item><description>For each seed file the template names, in ordinal order of its relative path: the path, a line feed, the
/// byte length in decimal, a line feed, the file's exact bytes (no line-ending or encoding change), then a line feed.</description></item>
/// </list>
/// <para>
/// The seed list is the set of paths the template names (the constitution draft, role charters and the Vault layout). Files in the
/// folder that the template does not name are not hashed. Changing one byte of a named seed changes the hash. Changing the
/// template version changes it too, because the version is part of the template model.
/// </para>
/// </remarks>
public static class TemplateHash
{
    private const string _header = "bazaar-template-hash/v1\n";

    /// <summary>The hash of a normalized template and the bytes of each seed it names. <paramref name="seeds"/> is keyed by relative path.</summary>
    public static string Compute(SetupTemplate template, IReadOnlyDictionary<string, byte[]> seeds)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(seeds);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Utf8(_header));

        var node = JsonSerializer.SerializeToNode(template, SetupTemplateJsonContext.Default.SetupTemplate);
        hash.AppendData(Utf8(SetupTemplateConstants.ManifestFileName + "\n"));
        hash.AppendData(SetupPlanCanonicalizer.CanonicalUtf8(node));
        hash.AppendData(Utf8("\n"));

        foreach (var path in seeds.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var bytes = seeds[path];
            hash.AppendData(Utf8(path + "\n" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\n"));
            hash.AppendData(bytes);
            hash.AppendData(Utf8("\n"));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
