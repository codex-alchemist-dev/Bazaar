using Bazaar.Client.Calcinato.Modal;
using Bazaar.Client.Core.Input;
using Bazaar.Ex;

namespace Bazaar.Client.Calcinato.Mnemonics;

/// <summary>
/// Hands leader sequences from the Calcinato modal engine to the client-wide mnemonic table (doc 41, Bazaar.Ex). A typed
/// sequence resolves to a command id from the validated table. The host runs the command through the callback and returns
/// the speech line. Pure apart from that callback.
/// </summary>
public sealed class ExMnemonicBridge : IMnemonicBridge
{
    private readonly MnemonicTable _table;
    private readonly Func<string, string?> _execute;

    private ExMnemonicBridge(MnemonicTable table, Func<string, string?> execute)
    {
        _table = table;
        _execute = execute;
    }

    /// <summary>
    /// Builds a bridge over a resolution. Returns null when the resolution has errors or the table cannot be built, so a
    /// broken set of mnemonics never reaches the terminal.
    /// </summary>
    /// <param name="resolution">The resolved effective assignments, from <see cref="MnemonicResolver"/>.</param>
    /// <param name="paletteKeys">The keys that open the palette. The table validates them.</param>
    /// <param name="execute">Runs a command id and returns the speech line, or null for a default.</param>
    public static ExMnemonicBridge? TryCreate(
        MnemonicResolution resolution,
        IEnumerable<KeyStroke> paletteKeys,
        Func<string, string?> execute)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(paletteKeys);
        ArgumentNullException.ThrowIfNull(execute);

        var table = resolution.BuildTable(paletteKeys);
        return table is null ? null : new ExMnemonicBridge(table, execute);
    }

    /// <inheritdoc />
    public bool TryHandle(string typedSequence, out string? speech)
    {
        var binding = _table.Exact(typedSequence);
        if (binding is null)
        {
            speech = null;
            return false;
        }

        speech = _execute(binding.CommandId);
        return true;
    }

    /// <inheritdoc />
    public bool IsPrefix(string typedSequence) =>
        _table.CandidatesFor(typedSequence).Any(binding => binding.Mnemonic.Length > typedSequence.Length);

    /// <inheritdoc />
    public bool IsExact(string typedSequence) => _table.Exact(typedSequence) is not null;
}
