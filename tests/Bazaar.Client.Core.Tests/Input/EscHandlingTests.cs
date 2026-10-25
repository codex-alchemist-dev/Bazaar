using Bazaar.Client.Core.Input;

namespace Bazaar.Client.Core.Tests.Input;

public class EscHandlingTests
{
    private static KeyRouter NewRouter() => new(InputTestData.Table());

    [Fact]
    public void Esc_cancels_a_pending_mnemonic_without_executing_it()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);
        router.Handle(InputTestData.C('c'), InputTestData.Normal);

        var result = router.Handle(InputTestData.Escape, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Cancelled, result.Outcome);
        Assert.Null(result.CommandId);
        Assert.Equal(InputMode.Normal, result.Mode);
        Assert.True(router.Chord.IsEmpty);
    }

    [Fact]
    public void Esc_then_enter_does_nothing_after_a_cancel()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);
        router.Handle(InputTestData.Escape, InputTestData.Normal);

        var result = router.Handle(InputTestData.Enter, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
    }

    [Fact]
    public void Esc_with_nothing_pending_in_normal_passes_through_to_the_head()
    {
        var result = NewRouter().Handle(InputTestData.Escape, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.Equal(InputMode.Normal, result.Mode);
    }

    [Fact]
    public void Esc_in_insert_releases_the_text_input_and_returns_to_normal()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('i'), InputTestData.FocusableOnly);

        var result = router.Handle(InputTestData.Escape, InputTestData.Focused);

        Assert.Equal(RouteOutcome.Consumed, result.Outcome);
        Assert.Equal(RouterActionKind.ReleaseTextFocus, result.Action!.Kind);
        Assert.Equal(InputMode.Normal, result.Mode);
    }

    [Fact]
    public void Esc_in_palette_closes_it_and_returns_to_normal()
    {
        var router = NewRouter();
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Space, InputTestData.Normal);
        router.Handle(InputTestData.Enter, InputTestData.Normal);

        var result = router.Handle(InputTestData.Escape, InputTestData.Normal);

        Assert.Equal(RouteOutcome.Consumed, result.Outcome);
        Assert.Equal(RouterActionKind.ClosePalette, result.Action!.Kind);
        Assert.Equal(InputMode.Normal, result.Mode);
    }

    [Fact]
    public void Esc_with_ctrl_held_is_not_treated_as_the_escape_key()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);

        var result = router.Handle(KeyStroke.OfKey(NamedKey.Escape, KeyModifiers.Control), InputTestData.Normal);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.Equal("w", router.Chord.Typed);
    }

    [Fact]
    public void Esc_during_ime_composition_is_left_to_the_ime()
    {
        var router = NewRouter();
        router.Handle(InputTestData.C('w'), InputTestData.Normal);
        var composing = new RouterContext(TextInputFocused: true, ImeComposing: true);

        var result = router.Handle(InputTestData.Escape, composing);

        Assert.Equal(RouteOutcome.Passthrough, result.Outcome);
        Assert.Equal("w", router.Chord.Typed);
    }
}
