using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Client.Core.Input;
using Bazaar.Ex.Head;

namespace Bazaar.Ex.Tests;

public class MnemonicRemapViewHeadlessTests
{
    private static readonly KeyStroke[] _palette = [KeyStroke.OfChar('p')];

    private static (Window Window, MnemonicRemapView View) Build()
    {
        var view = new MnemonicRemapView(
            NavigationDefaults.Assignments,
            MnemonicOverrideDocument.Empty,
            NavigationDefaults.SettingsCommandId,
            "Settings",
            _palette);
        var window = new Window { Content = view };
        window.Show();
        view.FocusInput();
        return (window, view);
    }

    [AvaloniaFact]
    public void Enter_with_a_free_mnemonic_raises_applied_with_the_new_document()
    {
        var (window, view) = Build();
        MnemonicOverrideDocument? applied = null;
        view.Applied += (_, document) => applied = document;
        view.Text = "zz";

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.NotNull(applied);
        Assert.Null(view.Reason);
        Assert.NotEqual(MnemonicOverrideDocument.Empty, view.Document);
    }

    [AvaloniaFact]
    public void Enter_with_a_taken_mnemonic_shows_the_reason_and_raises_nothing()
    {
        var (window, view) = Build();
        var raised = false;
        view.Applied += (_, _) => raised = true;
        var taken = NavigationDefaults.Assignments.First(a => a.CommandId != NavigationDefaults.SettingsCommandId && !a.Alias).Mnemonic;
        view.Text = taken;

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.False(raised);
        Assert.False(string.IsNullOrEmpty(view.Reason));
        Assert.Equal(MnemonicOverrideDocument.Empty, view.Document);
    }

    [AvaloniaFact]
    public void Escape_raises_cancelled()
    {
        var (window, view) = Build();
        var cancelled = false;
        view.Cancelled += (_, _) => cancelled = true;

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

        Assert.True(cancelled);
    }
}
