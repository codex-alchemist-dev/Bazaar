using Bazaar.Client.Input;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// The claimed-key table built from the client key router (doc 57 §7, claimed-key table). The editor holds focus, so the
/// router is in Insert mode; every candidate chord the router would not pass through is claimed. No Ctrl chord is a
/// candidate, so none is ever claimed. Call <see cref="Refresh"/> after the router's table changed.
/// </summary>
public sealed class RouterClaimedKeySource : IClaimedKeySource
{
    private readonly Func<KeyStroke, bool> _claims;
    private readonly HashSet<string> _excluded;
    private IReadOnlyList<string> _chords;

    /// <param name="claims">True when the router claims the stroke while a text input has focus.</param>
    /// <param name="excluded">Chords the operator keeps for the editor; they are neither pushed nor claimed.</param>
    public RouterClaimedKeySource(Func<KeyStroke, bool> claims, IEnumerable<string>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(claims);
        _claims = claims;
        _excluded = new HashSet<string>(excluded ?? [], StringComparer.Ordinal);
        _chords = Compute();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Chords => _chords;

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>A probe over a mnemonic table: a fresh router in Insert mode decides each stroke.</summary>
    public static Func<KeyStroke, bool> InsertModeProbe(MnemonicTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return stroke => new KeyRouter(table).Handle(stroke, new RouterContext(TextInputFocused: true, HasFocusableTextInput: true)).Outcome != RouteOutcome.Passthrough;
    }

    /// <summary>True when the key event's chord is in the table.</summary>
    public bool Claims(EditorKeyEvent key) => EditorKeyCodes.TryToChord(key, out var chord) && _chords.Contains(chord, StringComparer.Ordinal);

    /// <summary>Recomputes the table and raises <see cref="Changed"/> when it differs.</summary>
    public void Refresh()
    {
        var next = Compute();
        if (next.SequenceEqual(_chords, StringComparer.Ordinal))
        {
            return;
        }

        _chords = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string[] Compute()
    {
        var chords = new List<string>();
        foreach (var code in EditorKeyCodes.CandidateCodes)
        {
            for (var mask = 0; mask < 8; mask++)
            {
                bool shift = (mask & 1) != 0, alt = (mask & 2) != 0, meta = (mask & 4) != 0;
                if (!EditorKeyCodes.TryToKeyStroke(code, shift, alt, meta, out var stroke))
                {
                    continue;
                }

                var chord = EditorKeyCodes.ToChord(code, shift, alt, meta);
                if (!_excluded.Contains(chord) && Probe(stroke))
                {
                    chords.Add(chord);
                }
            }
        }

        return [.. chords];
    }

    private bool Probe(KeyStroke stroke)
    {
        try
        {
            return _claims(stroke);
        }
        catch (Exception)
        {
            return false; // a faulty router claims nothing
        }
    }
}
