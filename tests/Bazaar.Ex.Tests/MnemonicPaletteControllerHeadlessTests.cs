using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Client.Input;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicPaletteControllerHeadlessTests
{
    private static readonly IReadOnlyList<MnemonicListRow> _rows =
        MnemonicListing.Build(MnemonicResolver.Resolve(NavigationDefaults.Assignments).Effective);

    private sealed class Harness
    {
        public MnemonicPaletteView? Shown;
        public int Hidden;
        public List<string> Chosen { get; } = [];
        public MnemonicPaletteController Controller { get; }

        public Harness()
        {
            Controller = new MnemonicPaletteController(() => _rows, view => Shown = view, _ => Hidden++);
            Controller.CommandChosen += (_, id) => Chosen.Add(id);
        }
    }

    private static RouterAction Act(RouterActionKind kind, char? character = null) => new(kind, character);

    [AvaloniaFact]
    public void Open_builds_the_view_once_and_shows_it()
    {
        var h = new Harness();

        h.Controller.Handle(Act(RouterActionKind.OpenPalette));
        var first = h.Shown;
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));

        Assert.True(h.Controller.IsOpen);
        Assert.NotNull(first);
        Assert.Same(first, h.Shown);
        Assert.Equal(_rows.Count, first.Shown.Count);
    }

    [AvaloniaFact]
    public void Typed_characters_and_backspace_edit_the_query()
    {
        var h = new Harness();
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));

        h.Controller.Handle(Act(RouterActionKind.PaletteInput, 's'));
        h.Controller.Handle(Act(RouterActionKind.PaletteInput, 'e'));
        Assert.Equal("se", h.Shown!.Query);

        h.Controller.Handle(Act(RouterActionKind.PaletteBackspace));
        Assert.Equal("s", h.Shown.Query);
    }

    [AvaloniaFact]
    public void Backspace_on_an_empty_query_does_nothing()
    {
        var h = new Harness();
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));

        h.Controller.Handle(Act(RouterActionKind.PaletteBackspace));

        Assert.Equal(string.Empty, h.Shown!.Query);
    }

    [AvaloniaFact]
    public void Submit_closes_the_palette_and_reports_the_selected_command()
    {
        var h = new Harness();
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));
        h.Shown!.Query = "settings";
        var expected = h.Shown.Selected!.CommandId;

        h.Controller.Handle(Act(RouterActionKind.PaletteSubmit));

        Assert.False(h.Controller.IsOpen);
        Assert.Equal(1, h.Hidden);
        Assert.Equal([expected], h.Chosen);
    }

    [AvaloniaFact]
    public void Select_next_then_previous_moves_the_selection()
    {
        var h = new Harness();
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));
        var first = h.Shown!.Selected!.CommandId;

        h.Controller.Handle(Act(RouterActionKind.PaletteSelectNext));
        Assert.NotEqual(first, h.Shown.Selected!.CommandId);

        h.Controller.Handle(Act(RouterActionKind.PaletteSelectPrevious));
        Assert.Equal(first, h.Shown.Selected!.CommandId);
    }

    [AvaloniaFact]
    public void Close_hides_without_choosing_and_actions_while_closed_are_ignored()
    {
        var h = new Harness();
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));

        h.Controller.Handle(Act(RouterActionKind.ClosePalette));
        h.Controller.Handle(Act(RouterActionKind.PaletteInput, 'x'));
        h.Controller.Handle(Act(RouterActionKind.PaletteSubmit));
        h.Controller.Handle(Act(RouterActionKind.ClosePalette));

        Assert.False(h.Controller.IsOpen);
        Assert.Equal(1, h.Hidden);
        Assert.Empty(h.Chosen);
    }

    [AvaloniaFact]
    public void Dismissing_the_view_with_escape_closes_the_palette()
    {
        var h = new Harness();
        h.Controller.Handle(Act(RouterActionKind.OpenPalette));
        var window = new Window { Content = h.Shown };
        window.Show();
        h.Shown!.FocusQuery();

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.False(h.Controller.IsOpen);
        Assert.Equal(1, h.Hidden);
    }
}
