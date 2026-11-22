using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bazaar.Client.Input;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicRemapScreenHeadlessTests
{
    private static readonly KeyStroke[] _palette = [KeyStroke.OfChar('p')];

    private static (Window Window, MnemonicRemapScreen Screen) Build(
        IEnumerable<MnemonicAssignment>? assignments = null,
        MnemonicOverrideDocument? current = null,
        bool actorIsOwner = false)
    {
        var screen = new MnemonicRemapScreen(
            assignments ?? NavigationDefaults.Assignments,
            current ?? MnemonicOverrideDocument.Empty,
            _palette,
            actorIsOwner);
        var window = new Window { Content = screen };
        window.Show();
        return (window, screen);
    }

    [AvaloniaFact]
    public void Lists_each_command_once_with_its_default_mnemonic()
    {
        var (_, screen) = Build();

        var expected = NavigationDefaults.Assignments
            .Where(a => a.Tier != MnemonicTier.Person && !a.Alias)
            .Select(a => a.CommandId)
            .Distinct()
            .Count();

        Assert.Equal(expected, screen.Rows.Count);
        var settings = screen.Rows.Single(r => r.CommandId == NavigationDefaults.SettingsCommandId);
        Assert.Equal(NavigationDefaults.Assignments.First(a => a.CommandId == NavigationDefaults.SettingsCommandId && !a.Alias).Mnemonic, settings.Mnemonic);
        Assert.False(settings.Remapped);
    }

    [AvaloniaFact]
    public void Enter_with_a_free_mnemonic_raises_DocumentChanged_and_the_row_shows_the_new_mnemonic()
    {
        var (window, screen) = Build();
        MnemonicOverrideDocument? changed = null;
        screen.DocumentChanged += (_, document) => changed = document;
        Assert.True(screen.Select(NavigationDefaults.SettingsCommandId));
        screen.FocusList();

        // The remap prompt is the first focusable input in the editor; typing and Enter goes through it.
        var input = FindInput(window);
        input.Text = "zz";
        input.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.NotNull(changed);
        var row = screen.Rows.Single(r => r.CommandId == NavigationDefaults.SettingsCommandId);
        Assert.Equal("zz", row.Mnemonic);
        Assert.True(row.Remapped);
        Assert.Equal("'Settings' is saved.", screen.Status);
    }

    [AvaloniaFact]
    public void Reset_drops_the_remap_and_raises_DocumentChanged()
    {
        var current = new MnemonicOverrideDocument(
            MnemonicOverrideDocument.CurrentVersion,
            [new MnemonicOverride(NavigationDefaults.SettingsCommandId, "zz")]);
        var (window, screen) = Build(current: current);
        MnemonicOverrideDocument? changed = null;
        screen.DocumentChanged += (_, document) => changed = document;
        Assert.True(screen.Select(NavigationDefaults.SettingsCommandId));

        Assert.True(FindReset(window).IsEnabled);
        FindReset(window).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.NotNull(changed);
        Assert.Empty(changed!.Overrides);
        var row = screen.Rows.Single(r => r.CommandId == NavigationDefaults.SettingsCommandId);
        Assert.False(row.Remapped);
        Assert.False(FindReset(window).IsEnabled);
    }

    [AvaloniaFact]
    public void A_company_locked_command_cannot_be_remapped_by_a_member()
    {
        var target = NavigationDefaults.Assignments.First(a => a.CommandId != NavigationDefaults.SettingsCommandId && !a.Alias && a.Tier != MnemonicTier.Person).CommandId;
        var locked = NavigationDefaults.Assignments
            .Select(a => a.CommandId == target && !a.Alias ? a with { Tier = MnemonicTier.CompanyLocked } : a)
            .ToList();
        var (window, screen) = Build(assignments: locked);

        Assert.True(screen.Select(target));
        Assert.True(screen.Rows.Single(r => r.CommandId == target).Locked);
        Assert.Null(FindInputOrNull(window));
        Assert.False(FindReset(window).IsEnabled);
    }

    [AvaloniaFact]
    public void The_owner_can_remap_a_company_locked_command()
    {
        var target = NavigationDefaults.Assignments.First(a => a.CommandId != NavigationDefaults.SettingsCommandId && !a.Alias && a.Tier != MnemonicTier.Person).CommandId;
        var locked = NavigationDefaults.Assignments
            .Select(a => a.CommandId == target && !a.Alias ? a with { Tier = MnemonicTier.CompanyLocked } : a)
            .ToList();
        var (_, screen) = Build(assignments: locked, actorIsOwner: true);

        Assert.True(screen.Select(target));
        Assert.False(screen.Rows.Single(r => r.CommandId == target).Locked);
    }

    [AvaloniaFact]
    public void Escape_on_the_list_raises_Closed()
    {
        var (window, screen) = Build();
        var closed = false;
        screen.Closed += (_, _) => closed = true;

        // Raised on the screen itself, so the list's own Escape handling cannot swallow it first.
        screen.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = screen });

        Assert.True(closed);
    }

    private static TextBox FindInput(Window window) =>
        FindInputOrNull(window) ?? throw new InvalidOperationException("No remap prompt is open.");

    private static TextBox? FindInputOrNull(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();

    private static Button FindReset(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single();
}
