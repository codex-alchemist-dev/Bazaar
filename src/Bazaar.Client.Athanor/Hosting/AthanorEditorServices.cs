using Bazaar.Client.Input;
using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>The editor services a head hands the Athanor pane (doc 57 §2.2), and what to close at exit.</summary>
public sealed class AthanorEditorServices(
    IEditorHost host,
    IEditorFileSource source,
    IEditorDraftStore drafts,
    EditorHostKind kind,
    IReadOnlyList<string> notes,
    IAsyncDisposable? connection = null) : IAsyncDisposable
{
    public IEditorHost Host { get; } = host;

    public IEditorFileSource Source { get; } = source;

    public IEditorDraftStore Drafts { get; } = drafts;

    /// <summary>The host the selector chose.</summary>
    public EditorHostKind Kind { get; } = kind;

    /// <summary>Why hosts earlier in the order were not used, one line each.</summary>
    public IReadOnlyList<string> Notes { get; } = notes;

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync().ConfigureAwait(false);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The router table behind the claimed keys, for a head that runs no mnemonic runtime yet (doc 57 §2.2): built from the
/// commands the loaded modules contributed. The palette mnemonic is a placeholder, because Insert-mode claims do not depend
/// on it. Null when the table does not validate, which claims nothing.
/// </summary>
public static class EditorRouterTable
{
    public const string PlaceholderPaletteMnemonic = "palette";

    public static MnemonicTable? Create(IEnumerable<ClientCommandContribution> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return MnemonicTable.Create(
            PlaceholderPaletteMnemonic.Select(c => KeyStroke.OfChar(c)),
            commands.Select(c => new MnemonicBinding(c.Mnemonic, c.Id))).Table;
    }
}
