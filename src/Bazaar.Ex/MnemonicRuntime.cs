using Bazaar.Client.Input;

namespace Bazaar.Ex;

/// <summary>
/// The live mnemonic state for one person on one device: the defaults, the saved remaps, the resolved assignments and the
/// session the router reads. It loads the saved remaps once, applies remaps and resets through <see cref="MnemonicRemapper"/>,
/// saves each accepted change and rebuilds the session. A saved file that no longer resolves cleanly (a module was removed, or
/// the file was edited by hand) is ignored with a warning, so the defaults always stay reachable.
/// </summary>
public sealed class MnemonicRuntime
{
    private readonly IReadOnlyList<MnemonicAssignment> _defaults;
    private readonly IMnemonicOverrideStore _store;
    private readonly IReadOnlyList<KeyStroke> _paletteKeys;
    private readonly MnemonicNoticeLog _log;
    private readonly bool _actorIsOwner;
    private readonly IReadOnlyDictionary<string, string> _titles;

    private MnemonicRuntime(
        IReadOnlyList<MnemonicAssignment> defaults,
        IMnemonicOverrideStore store,
        IReadOnlyList<KeyStroke> paletteKeys,
        MnemonicNoticeLog log,
        bool actorIsOwner)
    {
        _defaults = defaults;
        _store = store;
        _paletteKeys = paletteKeys;
        _log = log;
        _actorIsOwner = actorIsOwner;
        _titles = defaults
            .GroupBy(a => a.CommandId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Title, StringComparer.Ordinal);
        Document = MnemonicOverrideDocument.Empty;
        Rows = [];
    }

    /// <summary>Raised after an accepted remap or reset has been saved and the session rebuilt.</summary>
    public event EventHandler? Changed;

    /// <summary>The session the router reads. Null only when the defaults themselves do not resolve.</summary>
    public MnemonicSession? Session { get; private set; }

    /// <summary>The rows the palette and the mnemonic list show.</summary>
    public IReadOnlyList<MnemonicListRow> Rows { get; private set; }

    /// <summary>The remaps now in force.</summary>
    public MnemonicOverrideDocument Document { get; private set; }

    /// <summary>A problem found while loading, such as an unreadable file or remaps that no longer resolve. Null when none.</summary>
    public string? Warning { get; private set; }

    /// <summary>Loads the saved remaps and builds the first session.</summary>
    public static async Task<MnemonicRuntime> CreateAsync(
        IEnumerable<MnemonicAssignment> defaults,
        IMnemonicOverrideStore store,
        IEnumerable<KeyStroke> paletteKeys,
        MnemonicNoticeLog log,
        bool actorIsOwner = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paletteKeys);
        ArgumentNullException.ThrowIfNull(log);

        var runtime = new MnemonicRuntime(
            defaults.Where(a => a.Tier != MnemonicTier.Person).ToList(),
            store,
            paletteKeys.ToList(),
            log,
            actorIsOwner);

        var load = await store.LoadAsync(ct).ConfigureAwait(false);
        runtime.Warning = load.Warning;
        if (!runtime.TryApply(load.Document))
        {
            runtime.Warning ??= "Saved mnemonic remaps no longer resolve and were ignored.";
            if (!runtime.TryApply(MnemonicOverrideDocument.Empty))
            {
                runtime.Warning = "The default mnemonics do not resolve; keyboard mnemonics are off.";
            }
        }

        return runtime;
    }

    /// <summary>Remaps one command for this person, saves it and rebuilds the session. A refused remap changes nothing.</summary>
    public async Task<MnemonicRemapResult> RemapAsync(string commandId, string mnemonic, CancellationToken ct = default)
    {
        var result = MnemonicRemapper.TryRemap(_defaults, Document, commandId, mnemonic, _paletteKeys, _actorIsOwner);
        return await CommitAsync(result, ct).ConfigureAwait(false);
    }

    /// <summary>Drops this person's remap of one command, saves it and rebuilds the session.</summary>
    public async Task<MnemonicRemapResult> ResetAsync(string commandId, CancellationToken ct = default) =>
        await CommitAsync(MnemonicRemapper.Reset(Document, commandId), ct).ConfigureAwait(false);

    private async Task<MnemonicRemapResult> CommitAsync(MnemonicRemapResult result, CancellationToken ct)
    {
        if (!result.Accepted)
        {
            return result;
        }

        await _store.SaveAsync(result.Document, ct).ConfigureAwait(false);
        if (!TryApply(result.Document))
        {
            return MnemonicRemapResult.Reject(Document, "The change was saved but does not resolve; the previous mnemonics stay in force.");
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private bool TryApply(MnemonicOverrideDocument document)
    {
        var assignments = new List<MnemonicAssignment>(_defaults);
        assignments.AddRange(FileMnemonicOverrideStore.ToAssignments(document, _titles));
        var resolution = MnemonicResolver.Resolve(assignments, _actorIsOwner);
        var session = MnemonicSession.TryCreate(resolution, _paletteKeys);
        if (session is null)
        {
            return false;
        }

        _log.Record(resolution.Notices);
        Session = session;
        Rows = MnemonicListing.Build(resolution.Effective);
        Document = document;
        return true;
    }
}
