using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.BrowserPool.Image;

/// <summary>One guest tool in a <see cref="PoolToolBundle"/>.</summary>
/// <param name="Name">The tool, one of <see cref="PoolToolBundle.AllowedNames"/>.</param>
/// <param name="FileName">The file name; always equal to <paramref name="Name"/>.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the file, 64 characters.</param>
/// <param name="Size">The file size in bytes; positive.</param>
public sealed record PoolToolEntry(string Name, string FileName, string Sha256, long Size);

/// <summary>
/// The manifest of the guest tools shipped into the pool VM (docs/design/59, section 11): which tools, and the SHA-256 and size of
/// each file. The provisioning script checks the hash before installing, and the readiness check compares the installed file to it.
/// It reads and writes strict JSON through source-generated <c>System.Text.Json</c>: unknown members, duplicate members, comments and
/// trailing commas are all refused.
/// </summary>
public sealed class PoolToolBundle
{
    /// <summary>The tool that spawns Chromium with the DevTools pipe.</summary>
    public const string PoolSpawnTool = "bazaar-pool-spawn";

    /// <summary>The tool that sends pointer input in the guest.</summary>
    public const string PointerTool = "bazaar-pointer";

    /// <summary>The file name of the manifest the publish script writes.</summary>
    public const string ManifestFileName = "pool-tools.json";

    /// <summary>The most bytes of one tool file that is hashed. A larger file fails closed.</summary>
    public const long MaxToolBytes = 64L * 1024 * 1024;

    /// <summary>The tool names a manifest may hold, in their fixed order.</summary>
    public static readonly IReadOnlyList<string> AllowedNames = [PoolSpawnTool, PointerTool];

    private PoolToolBundle(IReadOnlyList<PoolToolEntry> tools) => Tools = tools;

    /// <summary>The entries, in the order given.</summary>
    public IReadOnlyList<PoolToolEntry> Tools { get; }

    /// <summary>Returns the reasons the entries are unusable; empty when they are fine.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<PoolToolEntry>? tools)
    {
        var errors = new List<string>();
        if (tools is null || tools.Count == 0)
        {
            errors.Add("The bundle lists no tools.");
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tools)
        {
            if (t is null || t.Name is null || !AllowedNames.Contains(t.Name))
            {
                errors.Add("A tool name is not one of the allowed names.");
                continue;
            }

            if (!seen.Add(t.Name))
            {
                errors.Add($"The tool {t.Name} appears more than once.");
            }

            if (t.FileName != t.Name)
            {
                errors.Add($"The file name of {t.Name} must equal the tool name.");
            }

            if (!IsValidHash(t.Sha256))
            {
                errors.Add($"The hash of {t.Name} must be 64 lower-case hex characters.");
            }

            if (t.Size <= 0)
            {
                errors.Add($"The size of {t.Name} must be positive.");
            }
        }

        return errors;
    }

    /// <summary>True when the text is 64 lower-case hex characters.</summary>
    public static bool IsValidHash(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Builds a bundle from entries.</summary>
    /// <exception cref="ArgumentException">The entries are not valid.</exception>
    public static PoolToolBundle Create(IEnumerable<PoolToolEntry> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var list = tools.ToList();
        IReadOnlyList<string> errors = Validate(list);
        return errors.Count == 0 ? new PoolToolBundle(list) : throw new ArgumentException(string.Join(' ', errors), nameof(tools));
    }

    /// <summary>Looks one tool up by name.</summary>
    public PoolToolEntry? Find(string name) => Tools.FirstOrDefault(t => t.Name == name);

    /// <summary>Parses and validates a manifest. Never throws on bad input; the error never echoes the text.</summary>
    public static bool TryParse(string? json, out PoolToolBundle? bundle, out string error)
    {
        bundle = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "The manifest is empty.";
            return false;
        }

        BundleDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, PoolToolJsonContext.Default.BundleDto);
        }
        catch (JsonException)
        {
            error = "The manifest is not valid JSON of the expected shape.";
            return false;
        }

        if (dto?.Tools is null || dto.Tools.Any(t => t is null || t.Name is null || t.File is null || t.Sha256 is null || t.Size is null))
        {
            error = "The manifest has a tool with a missing field.";
            return false;
        }

        var entries = dto.Tools.Select(t => new PoolToolEntry(t!.Name!, t.File!, t.Sha256!, t.Size!.Value)).ToList();
        IReadOnlyList<string> errors = Validate(entries);
        if (errors.Count != 0)
        {
            error = string.Join(' ', errors);
            return false;
        }

        bundle = new PoolToolBundle(entries);
        error = string.Empty;
        return true;
    }

    /// <summary>Writes the manifest as indented JSON ending in a newline.</summary>
    public string ToJson()
    {
        var dto = new BundleDto { Tools = Tools.Select(t => new ToolDto { Name = t.Name, File = t.FileName, Sha256 = t.Sha256, Size = t.Size }).Cast<ToolDto?>().ToList() };
        return JsonSerializer.Serialize(dto, PoolToolJsonContext.Default.BundleDto) + "\n";
    }

    /// <summary>
    /// Hashes every allowed tool found in <paramref name="directory"/> (paths are joined with '/'). Each tool must be present and no
    /// larger than <see cref="MaxToolBytes"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A tool file is missing, empty or too large.</exception>
    public static PoolToolBundle FromDirectory(string directory, IPoolImageFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var entries = new List<PoolToolEntry>();
        foreach (var name in AllowedNames)
        {
            string path = directory.TrimEnd('/') + "/" + name;
            if (!fileSystem.FileExists(path))
            {
                throw new InvalidOperationException($"The tool file {name} is not in the directory.");
            }

            (string hash, long size) = PoolFileHash.Sha256(fileSystem, path);
            entries.Add(new PoolToolEntry(name, name, hash, size));
        }

        try
        {
            return Create(entries);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    internal sealed class BundleDto
    {
        public List<ToolDto?>? Tools { get; set; }
    }

    internal sealed class ToolDto
    {
        public string? Name { get; set; }

        public string? File { get; set; }

        public string? Sha256 { get; set; }

        public long? Size { get; set; }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
    WriteIndented = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(PoolToolBundle.BundleDto))]
internal sealed partial class PoolToolJsonContext : JsonSerializerContext;

/// <summary>Streams a file through SHA-256 with a size cap, using the file port.</summary>
internal static class PoolFileHash
{
    /// <summary>Returns the lower-case hex hash and the byte count.</summary>
    /// <exception cref="InvalidOperationException">The file is empty or larger than <see cref="PoolToolBundle.MaxToolBytes"/>.</exception>
    public static (string Hash, long Size) Sha256(IPoolImageFileSystem fs, string path)
    {
        if (fs.GetFileLength(path) > PoolToolBundle.MaxToolBytes)
        {
            throw new InvalidOperationException("The file is larger than the hash limit.");
        }

        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        using Stream stream = fs.OpenRead(path);
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > PoolToolBundle.MaxToolBytes)
            {
                throw new InvalidOperationException("The file is larger than the hash limit.");
            }

            hash.AppendData(buffer, 0, n);
        }

        if (total == 0)
        {
            throw new InvalidOperationException("The file is empty.");
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
    }
}
