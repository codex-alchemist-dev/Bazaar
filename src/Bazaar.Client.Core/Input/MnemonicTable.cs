namespace Bazaar.Client.Core.Input;

public sealed record MnemonicBinding(string Mnemonic, string CommandId);

public sealed record MnemonicTableResult(MnemonicTable? Table, IReadOnlyList<string> Errors);

/// <summary>
/// The validated set of mnemonics for one client. Built once at load; conflicts fail the load.
/// A mnemonic may be a prefix of another (for example "w" and "wc"): the router resolves
/// that by waiting for Enter, so prefixes are allowed here.
/// </summary>
public sealed class MnemonicTable
{
    /// <summary>Command id for the palette-opening mnemonic. The router handles it itself.</summary>
    public const string PaletteCommandId = "client.palette.open";

    /// <summary>'i' with nothing typed always enters Insert, so no mnemonic may start with it.</summary>
    public const char ReservedInsertKey = 'i';

    private readonly IReadOnlyList<MnemonicBinding> _bindings;

    private MnemonicTable(string paletteMnemonic, IReadOnlyList<MnemonicBinding> bindings)
    {
        PaletteMnemonic = paletteMnemonic;
        _bindings = bindings;
    }

    public string PaletteMnemonic { get; }

    public IReadOnlyList<MnemonicBinding> Bindings => _bindings;

    public static MnemonicTableResult Create(IEnumerable<KeyStroke> paletteKeys, IEnumerable<MnemonicBinding> commands)
    {
        var errors = new List<string>();
        var paletteText = ValidateOne(paletteKeys.ToArray(), PaletteCommandId, errors);
        var all = new List<MnemonicBinding>();
        if (paletteText is not null)
        {
            all.Add(new MnemonicBinding(paletteText, PaletteCommandId));
        }

        foreach (var command in commands)
        {
            var keys = command.Mnemonic.Select(c => KeyStroke.OfChar(c)).ToArray();
            var text = ValidateOne(keys, command.CommandId, errors);
            if (text is not null)
            {
                all.Add(command with { Mnemonic = text });
            }
        }

        foreach (var group in all.GroupBy(binding => binding.Mnemonic, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            var ids = string.Join(", ", group.Select(binding => binding.CommandId));
            errors.Add($"Mnemonic '{group.Key}' is bound more than once: {ids}.");
        }

        if (errors.Count > 0)
        {
            return new MnemonicTableResult(null, errors);
        }

        return new MnemonicTableResult(new MnemonicTable(paletteText!, all), []);
    }

    /// <summary>Bindings whose mnemonic starts with the typed text, in declaration order.</summary>
    public IReadOnlyList<MnemonicBinding> CandidatesFor(string typed) =>
        _bindings.Where(binding => binding.Mnemonic.StartsWith(typed, StringComparison.Ordinal)).ToList();

    public MnemonicBinding? Exact(string typed) =>
        _bindings.FirstOrDefault(binding => string.Equals(binding.Mnemonic, typed, StringComparison.Ordinal));

    private static string? ValidateOne(KeyStroke[] keys, string commandId, List<string> errors)
    {
        var problem = Mnemonic.Validate(keys);
        if (problem is not null)
        {
            errors.Add($"Mnemonic for '{commandId}' is invalid: {problem}");
            return null;
        }

        var text = Mnemonic.ToText(keys);
        if (text[0] == ReservedInsertKey)
        {
            errors.Add($"Mnemonic '{text}' for '{commandId}' starts with the reserved Insert key '{ReservedInsertKey}'.");
            return null;
        }

        return text;
    }
}
