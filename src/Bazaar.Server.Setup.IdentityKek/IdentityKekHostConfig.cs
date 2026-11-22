using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Setup.IdentityKek;

/// <summary>
/// The host configuration file that carries <c>identity.kek_provider</c> (doc 45 step 5, PROPOSED). The KEK opens before the database,
/// so its provider kind is host configuration, not a settings-registry value. Setup owns this one small file and writes only the
/// provider kind into it. The TPM and OpenBao options stay with the operator's own configuration. The host adds the file as an
/// optional JSON source after it knows the data root, so a value in the environment or in <c>appsettings.json</c> can still be
/// overridden by an operator who wants to.
/// </summary>
public static class IdentityKekHostConfig
{
    /// <summary>The configuration key the KEK module reads (<c>KekComponent.KekProviderKey</c>).</summary>
    public const string KekProviderKey = "Bazaar:Identity:KekProvider";

    /// <summary>The file, relative to the data root.</summary>
    public const string RelativePath = "config/kek.json";

    /// <summary>The provider kinds a plan may name (doc 14). Whether a kind is built into the running server is the KEK module's concern.</summary>
    public static IReadOnlyList<string> AllowedKinds { get; } = ["file", "dpapi", "openbao", "tpm"];

    /// <summary>The absolute path of the file under <paramref name="dataRoot"/>.</summary>
    public static string PathFor(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, "config", "kek.json");
    }

    /// <summary>The file body for <paramref name="kind"/>: the nested form of <see cref="KekProviderKey"/>. Contains no secret.</summary>
    public static byte[] Render(string kind)
    {
        if (!AllowedKinds.Contains(kind, StringComparer.Ordinal))
        {
            throw new ArgumentException("The identity KEK provider must be file, dpapi, openbao or tpm.", nameof(kind));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("Bazaar");
            writer.WriteStartObject("Identity");
            writer.WriteString("KekProvider", kind);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return [.. stream.ToArray(), .. Encoding.UTF8.GetBytes("\n")];
    }

    /// <summary>Writes the file atomically (temp file, then move) and returns the previous bytes, or null when there was no file.</summary>
    public static byte[]? Write(string dataRoot, string kind)
    {
        var path = PathFor(dataRoot);
        var body = Render(kind);
        var previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        if (previous is not null && previous.AsSpan().SequenceEqual(body))
        {
            return previous;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, body);
        File.Move(temp, path, overwrite: true);
        return previous;
    }

    /// <summary>Puts the file back as it was: the old bytes, or no file when <paramref name="previous"/> is null.</summary>
    public static void Restore(string dataRoot, byte[]? previous)
    {
        var path = PathFor(dataRoot);
        if (previous is null)
        {
            File.Delete(path);
            return;
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp, previous);
        File.Move(temp, path, overwrite: true);
    }
}
