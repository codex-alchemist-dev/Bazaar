using Bazaar.Client.Core.Input;

namespace Bazaar.Client.Core.Tests.Input;

public class KeyRouterStateTests
{
    private static KeyRouter NewRouter() => new(InputTestData.Table());

    [Fact]
    public void Typing_a_mnemonic_is_pending_until_enter()
    {
        var router = NewRouter();

        var typed = router.Handle(InputTestData.C('g'), InputTestData.Normal);
        Assert.Equal(RouteOutcome.Pending, typed.Outcome);
        Assert.Equal("g", router.Chord.Typed);

        var typedMore = router.Handle(InputTestData.C('c'), InputTestData.Normal);
        Assert.Equal(RouteOutcome.Pending, typedMore.Outcome);
        Assert.Equal("gc", router.Chord.Typed);
    }

    [Fact]
    public void Enter_executes_the_exact_mnemonic()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('g'), InputTestData.Normal);
        router.Handle(InputTestData.C('c'), InputTestData.Normal);

        var result = router.Handle(InputTestData.Enter, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Executed, result.Outcome);
        Assert.Equal("gc.open", result.CommandId);
        Assert.Equal(InputMode.Normal, result.Mode);
        Assert.True(result.Chord.IsEmpty);
    }

    [Fact]
    public void Enter_with_nothing_typed_passes_through()
    {
        var result = NewRouter().Handle(InputTestData.Enter, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void Enter_with_a_prefix_that_is_not_a_mnemonic_is_no_match_and_clears()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('g'), InputTestData.Normal);

        var result = router.Handle(InputTestData.Enter, InputTestData.Normal);

        Assert.Equal(RouteOutcome.NoMatch, result.Outcome);
        Assert.Null(result.CommandId);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void A_character_that_extends_no_mnemonic_is_no_match_and_clears()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);

        var result = router.Handle(InputTestData.C('x'), InputTestData.Normal);

        Assert.Equal(RouteOutcome.NoMatch, result.Outcome);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Hints_list_every_binding_the_typed_text_can_still_become()
    {
        var router = NewRouter();

        var result = router.Handle(InputTestData.C('w'), InputTestData.Normal);

        Assert.Equal(["w", "wc", "wi"], result.Chord.Hints.Select(hint => hint.Mnemonic));
    }

    [Fact]
    public void Backspace_removes_the_last_typed_key_and_cancels_when_empty()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);
        router.Handle(InputTestData.C('c'), InputTestData.Normal);

        var shorter = router.Handle(InputTestData.Backspace, InputTestData.Normal);
        Assert.Equal(RouteOutcome.Pending, shorter.Outcome);
        Assert.Equal("w", router.Chord.Typed);

        var empty = router.Handle(InputTestData.Backspace, InputTestData.Normal);
        Assert.Equal(RouteOutcome.Cancelled, empty.Outcome);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Another_named_key_ends_a_pending_mnemonic_and_passes_through()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);

        var result = router.Handle(InputTestData.Down, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Ctrl_chords_in_normal_mode_pass_through_and_leave_pending_alone()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);

        var result = router.Handle(KeyStroke.OfChar('c', KeyModifiers.Control), InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.Equal("w", router.Chord.Typed);
    }

    [Fact]
    public void I_with_a_focusable_text_input_enters_insert_and_asks_for_focus()
    {
        var router = NewRouter();

        var result = router.Handle(InputTestData.C('i'), InputTestData.FocusableOnly);

        Assert.Equal(RouteOutcome.Consumed, result.Outcome);
        Assert.Equal(InputMode.Insert, result.Mode);
        Assert.Equal(RouterActionKind.FocusTextInput, result.Action!.Kind);
    }

    [Fact]
    public void I_with_nothing_focusable_stays_in_normal_and_reports_no_target()
    {
        var router = NewRouter();

        var result = router.Handle(InputTestData.C('i'), InputTestData.Normal);

        Assert.Equal(RouteOutcome.NoTarget, result.Outcome);
        Assert.Equal(InputMode.Normal, result.Mode);
        Assert.Null(result.Action);
    }

    [Fact]
    public void Any_focused_text_input_is_insert_mode_and_letters_are_not_commands()
    {
        var router = NewRouter();

        var letter = router.Handle(InputTestData.C('w'), InputTestData.Focused);

        Assert.Equal(InputMode.Insert, letter.Mode);
        Assert.Equal(RouteOutcome.Passthrough, letter.Outcome);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Steno_letters_reach_the_focused_field_and_are_not_commands_when_something_is_focused()
    {
        var router = NewRouter();

        foreach (var letter in "gcwi")
        {
            var result = router.Handle(InputTestData.C(letter), InputTestData.Focused);
            Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
            Assert.Null(result.CommandId);
        }
    }

    [Fact]
    public void Focus_moving_into_a_text_input_cancels_a_pending_mnemonic()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);

        var result = router.Handle(InputTestData.C('x'), InputTestData.Focused);

        Assert.Equal(InputMode.Insert, result.Mode);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Losing_focus_while_in_insert_returns_to_normal()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('i'), InputTestData.FocusableOnly);

        var result = router.Handle(InputTestData.C('g'), InputTestData.Normal);

        Assert.Equal(InputMode.Normal, result.Mode);
        Assert.Equal("g", router.Chord.Typed);
    }

    [Fact]
    public void Space_Space_then_enter_opens_the_palette()
    {
        var router = NewRouter();
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Space, InputTestData.Normal);

        var result = router.Handle(InputTestData.Enter, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Executed, result.Outcome);
        Assert.Equal(InputMode.Palette, result.Mode);
        Assert.Equal(RouterActionKind.OpenPalette, result.Action!.Kind);
        Assert.Equal(MnemonicTable.PaletteCommandId, result.CommandId);
    }

    [Fact]
    public void Palette_takes_query_text_moves_selection_and_submits_back_to_normal()
    {
        var router = NewRouter();
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Enter, InputTestData.Normal);

        var typed = router.Handle(InputTestData.C('c'), InputTestData.Normal);
        Assert.Equal(RouterActionKind.PaletteInput, typed.Action!.Kind);
        Assert.Equal('c', typed.Action.Character);
        Assert.Equal(InputMode.Palette, typed.Mode);

        Assert.Equal(RouterActionKind.PaletteSelectNext, router.Handle(InputTestData.Down, InputTestData.Normal).Action!.Kind);
        Assert.Equal(RouterActionKind.PaletteSelectPrevious, router.Handle(InputTestData.Up, InputTestData.Normal).Action!.Kind);
        Assert.Equal(RouterActionKind.PaletteBackspace, router.Handle(InputTestData.Backspace, InputTestData.Normal).Action!.Kind);

        var submit = router.Handle(InputTestData.Enter, InputTestData.Normal);
        Assert.Equal(RouterActionKind.PaletteSubmit, submit.Action!.Kind);
        Assert.Equal(InputMode.Normal, submit.Mode);
    }

    [Fact]
    public void Palette_ignores_ctrl_chords_and_passes_them_through()
    {
        var router = NewRouter();
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Enter, InputTestData.Normal);

        var result = router.Handle(KeyStroke.OfChar('v', KeyModifiers.Control), InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.Equal(InputMode.Palette, result.Mode);
    }

    [Fact]
    public void Palette_stays_open_while_a_text_input_has_focus()
    {
        var router = NewRouter();
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Enter, InputTestData.Normal);

        var result = router.Handle(InputTestData.C('x'), InputTestData.Focused);

        Assert.Equal(InputMode.Palette, result.Mode);
        Assert.Equal(RouterActionKind.PaletteInput, result.Action!.Kind);
    }

    [Fact]
    public void Ime_composition_bypasses_normal_mode()
    {
        var router = NewRouter();
        var composing = new RouterContext(ImeComposing: true, HasFocusableTextInput: true);

        var letter = router.Handle(InputTestData.C('w'), composing);
        var escape = router.Handle(InputTestData.Escape, composing);
        var enter = router.Handle(InputTestData.Enter, composing);

        Assert.Equal(RouteOutcome.Passthrough, letter.Outcome);
        Assert.Equal(RouteOutcome.Passthrough, escape.Outcome);
        Assert.Equal(RouteOutcome.Passthrough, enter.Outcome);
        Assert.Equal(InputMode.Normal, router.Mode);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Ime_composition_bypasses_palette_and_insert()
    {
        var router = NewRouter();
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Enter, InputTestData.Normal);
        var composing = new RouterContext(TextInputFocused: true, ImeComposing: true);

        var result = router.Handle(InputTestData.C('k'), composing);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.Equal(InputMode.Palette, result.Mode);
    }
}
