using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Ex;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicPaletteViewHeadlessTests
{
    private static readonly IReadOnlyList<MnemonicListRow> _rows =
        MnemonicListing.Build(MnemonicResolver.Resolve(NavigationDefaults.Assignments).Effective);

    private static (Window Window, MnemonicPaletteView View) Build()
    {
        var view = new MnemonicPaletteView(_rows);
        var window = new Window { Content = view };
        window.Show();
        view.FocusQuery();
        return (window, view);
    }

    [AvaloniaFact]
    public void Empty_query_shows_every_row_with_the_first_selected()
    {
        var (_, view) = Build();

        Assert.Equal(_rows.Count, view.Shown.Count);
        Assert.Equal(_rows[0].CommandId, view.Selected!.CommandId);
    }

    [AvaloniaFact]
    public void Typing_narrows_the_list_to_matching_rows()
    {
        var (_, view) = Build();

        view.Query = "settings";

        Assert.Contains(view.Shown, row => row.CommandId == NavigationDefaults.SettingsCommandId);
        Assert.True(view.Shown.Count < _rows.Count);
    }

    [AvaloniaFact]
    public void Down_moves_the_selection_and_Enter_chooses_it()
    {
        var (window, view) = Build();
        var chosen = new List<string>();
        view.CommandSelected += (_, id) => chosen.Add(id);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.Single(chosen);
        Assert.Equal(_rows[1].CommandId, chosen[0]);
    }

    [AvaloniaFact]
    public void Escape_dismisses_without_choosing()
    {
        var (window, view) = Build();
        var chosen = new List<string>();
        var dismissed = 0;
        view.CommandSelected += (_, id) => chosen.Add(id);
        view.Dismissed += (_, _) => dismissed++;

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.Equal(1, dismissed);
        Assert.Empty(chosen);
    }

    [AvaloniaFact]
    public void Enter_with_no_match_chooses_nothing()
    {
        var (window, view) = Build();
        var chosen = new List<string>();
        view.CommandSelected += (_, id) => chosen.Add(id);
        view.Query = "zzzqqq";

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.Empty(view.Shown);
        Assert.Empty(chosen);
    }
}
