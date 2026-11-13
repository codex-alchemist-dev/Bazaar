using System.Text.Json;

namespace Bazaar.Client.Components.Navigation;

/// <summary>One person's remap of a command's mnemonic on one device.</summary>
public sealed record MnemonicOverride(string CommandId, string Mnemonic);

/// <summary>The saved remaps. Version guards the file layout; an unknown version is ignored, never guessed at.</summary>
public sealed record MnemonicOverrideDocument(int Version, IReadOnlyList<MnemonicOverride> Overrides)
{
    public const int CurrentVersion = 1;

    public static MnemonicOverrideDocument Empty { get; } = new(CurrentVersion, []);
}

/// <summary>Result of a load. A bad file yields an empty document and a warning, never an exception.</summary>
public sealed record MnemonicOverrideLoad(MnemonicOverrideDocument Document, string? Warning);

/// <summary>Per-person, per-device storage for remaps. The caller chooses the location.</summary>
public interface IMnemonicOverrideStore
{
    Task<MnemonicOverrideLoad> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(MnemonicOverrideDocument document, CancellationToken ct = default);
}

/// <summary>JSON file store. Saves go to a temp file, then replace the target, so a crash never leaves a half-written file.</summary>
public sealed class FileMnemonicOverrideStore : IMnemonicOverrideStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    public FileMnemonicOverrideStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    public async Task<MnemonicOverrideLoad> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path))
        {
            return new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, null);
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<MnemonicOverrideDocument>(stream, _options, ct).ConfigureAwait(false);
            if (document is null || document.Version != MnemonicOverrideDocument.CurrentVersion)
            {
                return new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, "Mnemonic overrides file has an unknown version and was ignored.");
            }

            return new MnemonicOverrideLoad(document with { Overrides = document.Overrides ?? [] }, null);
        }
        catch (JsonException)
        {
            return new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, "Mnemonic overrides file is not valid and was ignored.");
        }
        catch (IOException)
        {
            return new MnemonicOverrideLoad(MnemonicOverrideDocument.Empty, "Mnemonic overrides file could not be read and was ignored.");
        }
    }

    public async Task SaveAsync(MnemonicOverrideDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);

        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, document with { Version = MnemonicOverrideDocument.CurrentVersion }, _options, ct)
                .ConfigureAwait(false);
        }

        File.Move(temp, _path, overwrite: true);
    }

    /// <summary>
    /// Turns saved remaps into person-tier assignments. Entries whose command is unknown are kept out,
    /// so a module removed since the save does not block the rest.
    /// </summary>
    public static IReadOnlyList<MnemonicAssignment> ToAssignments(
        MnemonicOverrideDocument document,
        IReadOnlyDictionary<string, string> titlesByCommandId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(titlesByCommandId);

        return document.Overrides
            .Where(o => titlesByCommandId.ContainsKey(o.CommandId))
            .Select(o => new MnemonicAssignment(o.CommandId, titlesByCommandId[o.CommandId], o.Mnemonic, MnemonicTier.Person, "you"))
            .ToList();
    }
}
