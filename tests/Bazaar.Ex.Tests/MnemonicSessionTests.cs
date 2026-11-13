using Bazaar.Ex;
using Bazaar.Client.Core.Input;

namespace Bazaar.Ex.Tests;

public class MnemonicSessionTests
{
    private static MnemonicSession NewSession()
    {
        var resolution = MnemonicResolver.Resolve(NavigationDefaults.Assignments);
        return MnemonicSession.TryCreate(resolution, [KeyStroke.OfChar('p')])!;
    }

    private static MnemonicStep Type(MnemonicSession session, string text, RouterContext? context = null)
    {
        MnemonicStep last = null!;
        foreach (var c in text)
        {
            last = session.Handle(KeyStroke.OfChar(c), context ?? new RouterContext());
        }

        return last;
    }

    [Fact]
    public void Typing_a_navigation_mnemonic_shows_the_overlay_and_Enter_runs_it()
    {
        var session = NewSession();

        var typed = Type(session, "ngc1");
        Assert.Equal(RouteOutcome.Pending, typed.Outcome);
        Assert.Equal("Move to: GC list, first chat", typed.Overlay);

        var run = session.Handle(KeyStroke.OfKey(NamedKey.Enter), new RouterContext());
        Assert.Equal(RouteOutcome.Executed, run.Outcome);
        Assert.Equal(NavigationDefaults.GcFirstCommandId, run.CommandId);
        Assert.Equal("GC list, first chat.", run.Speech);
        Assert.Null(run.Overlay);
    }

    [Fact]
    public void A_prefix_with_no_exact_binding_has_no_overlay_yet()
    {
        var step = Type(NewSession(), "n");

        Assert.Equal(RouteOutcome.Pending, step.Outcome);
        Assert.Null(step.Overlay);
    }

    [Fact]
    public void Unknown_text_is_an_assertive_announcement()
    {
        // "x" starts no mnemonic, so the router answers at once.
        var step = NewSession().Handle(KeyStroke.OfChar('x'), new RouterContext());

        Assert.Equal(RouteOutcome.NoMatch, step.Outcome);
        Assert.Equal(AnnouncePriority.Assertive, step.Priority);
        Assert.Equal("No mnemonic matches.", step.Speech);
    }

    [Fact]
    public void I_with_nothing_to_focus_is_refused_and_announced_assertively()
    {
        var step = NewSession().Handle(KeyStroke.OfChar('i'), new RouterContext(HasFocusableTextInput: false));

        Assert.Equal(RouteOutcome.NoTarget, step.Outcome);
        Assert.Equal(AnnouncePriority.Assertive, step.Priority);
    }

    [Fact]
    public void Typing_in_a_text_input_passes_through_and_runs_no_mnemonic()
    {
        var session = NewSession();
        var context = new RouterContext(TextInputFocused: true, HasFocusableTextInput: true);

        var step = session.Handle(KeyStroke.OfChar('n'), context);

        Assert.Equal(RouteOutcome.Passthrough, step.Outcome);
        Assert.Equal(InputMode.Insert, session.Mode);
    }
}
