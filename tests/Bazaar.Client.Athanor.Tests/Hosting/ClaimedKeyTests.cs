using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Input;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public sealed class ClaimedKeyTests
{
    private static MnemonicTable Table() =>
        MnemonicTable.Create([KeyStroke.OfChar('p')], [new MnemonicBinding("ed", "athanor.open")]).Table!;

    [Theory]
    [InlineData("KeyM", "KeyM")]
    [InlineData("M", "KeyM")]
    [InlineData("m", "KeyM")]
    [InlineData("D1", "Digit1")]
    [InlineData("Digit1", "Digit1")]
    [InlineData("Return", "Enter")]
    [InlineData("Up", "ArrowUp")]
    [InlineData("Next", "PageDown")]
    [InlineData("F6", "F6")]
    public void Both_name_schemes_normalise_to_codes(string key, string code)
    {
        Assert.True(EditorKeyCodes.TryNormalize(key, out var actual));
        Assert.Equal(code, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Semicolon")]
    [InlineData("F13")]
    public void Unknown_names_do_not_normalise(string key) => Assert.False(EditorKeyCodes.TryNormalize(key, out _));

    [Fact]
    public void Chord_order_is_alt_meta_shift_code()
    {
        Assert.Equal("Alt+Meta+Shift+KeyM", EditorKeyCodes.ToChord("KeyM", shift: true, alt: true, meta: true));
        Assert.False(EditorKeyCodes.TryToChord(new EditorKeyEvent("KeyS", false, false, false, true), out _));
    }

    [Fact]
    public void Key_strokes_carry_shifted_characters_and_named_keys()
    {
        Assert.True(EditorKeyCodes.TryToKeyStroke(new EditorKeyEvent("KeyW", true, false, false, false), out var shifted));
        Assert.Equal('W', shifted.Character);
        Assert.True(EditorKeyCodes.TryToKeyStroke(new EditorKeyEvent("Escape", false, false, false, false), out var esc));
        Assert.Equal(NamedKey.Escape, esc.Named);
        Assert.False(EditorKeyCodes.TryToKeyStroke(new EditorKeyEvent("KeyS", false, false, false, true), out _));
        Assert.False(EditorKeyCodes.TryToKeyStroke(new EditorKeyEvent("F6", false, false, false, false), out _));
    }

    [Fact]
    public void Router_in_insert_mode_claims_only_escape()
    {
        var source = new RouterClaimedKeySource(RouterClaimedKeySource.InsertModeProbe(Table()));

        Assert.Equal(["Shift+Escape", "Escape"], source.Chords.OrderByDescending(c => c.Length));
        Assert.DoesNotContain(source.Chords, c => c.Contains("Ctrl", StringComparison.Ordinal));
    }

    [Fact]
    public void A_probe_that_claims_everything_still_yields_no_ctrl_chord_and_ignores_faults()
    {
        var all = new RouterClaimedKeySource(_ => true);
        Assert.NotEmpty(all.Chords);
        Assert.DoesNotContain(all.Chords, c => c.Split('+').Contains("Ctrl"));
        Assert.Contains("Alt+KeyM", all.Chords);

        var faulty = new RouterClaimedKeySource(_ => throw new InvalidOperationException());
        Assert.Empty(faulty.Chords);
    }

    [Fact]
    public void Excluded_chords_are_not_pushed_or_claimed()
    {
        var source = new RouterClaimedKeySource(RouterClaimedKeySource.InsertModeProbe(Table()), ["Escape"]);
        Assert.Equal(["Shift+Escape"], source.Chords);
        Assert.False(source.Claims(new EditorKeyEvent("Escape", false, false, false, false)));
        Assert.True(source.Claims(new EditorKeyEvent("Escape", true, false, false, false)));
    }

    [Fact]
    public void Refresh_raises_changed_only_when_the_table_differs()
    {
        var claim = false;
        var source = new RouterClaimedKeySource(s => claim && s.Named == NamedKey.Tab && s.Modifiers == KeyModifiers.None);
        var raised = 0;
        source.Changed += (_, _) => raised++;

        source.Refresh();
        Assert.Equal(0, raised);
        claim = true;
        source.Refresh();
        Assert.Equal(1, raised);
        Assert.Equal(["Tab"], source.Chords);
        source.Refresh();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void Handoff_for_strokes_uses_the_same_conversion_for_both_name_schemes()
    {
        var handoff = RouterKeyHandoff.ForStrokes(s => s.Named == NamedKey.Escape);

        Assert.True(handoff.TryClaim(new EditorKeyEvent("Escape", false, false, false, false)));
        Assert.False(handoff.TryClaim(new EditorKeyEvent("KeyA", false, false, false, false)));
        Assert.False(handoff.TryClaim(new EditorKeyEvent("Escape", false, false, false, true)));
        var skipping = RouterKeyHandoff.ForStrokes(s => s.Named == NamedKey.Escape, ["Escape"]);
        Assert.False(skipping.TryClaim(new EditorKeyEvent("Escape", false, false, false, false)));
    }
}
