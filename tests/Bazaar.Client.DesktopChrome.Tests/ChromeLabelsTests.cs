using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Bazaar.Localisation;

namespace Bazaar.Client.DesktopChrome.Tests;

public class ChromeLabelsTests
{
    private static MessageCatalog German() => ChromeCatalog.FromJson("de", new MemoryStream(Encoding.UTF8.GetBytes("""
        {
          "chrome.sidebar.name": "Gruppenchats",
          "chrome.sidebar.collapse": "Seitenleiste einklappen",
          "chrome.sidebar.expand": "Seitenleiste ausklappen",
          "chrome.sheet.opened": "{title}, Dialog geöffnet"
        }
        """)));

    [Fact]
    public void Every_key_the_chrome_uses_exists_in_the_english_catalogue()
    {
        foreach (string key in ChromeKeys.All)
        {
            Assert.True(ChromeCatalog.English.TryGet(key, out _), $"missing in chrome.en.json: {key}");
        }
    }

    [Fact]
    public void Every_key_constant_is_listed_and_the_catalogue_holds_no_unused_key()
    {
        string[] constants = [.. typeof(ChromeKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name != nameof(ChromeKeys.TitleArgument))
            .Select(f => (string)f.GetRawConstantValue()!)];

        Assert.Equal(constants.Order(), ChromeKeys.All.Order());
        Assert.Equal(constants.Order(), ChromeCatalog.English.Keys.Order());
    }

    [Fact]
    public void English_is_the_default_and_no_message_resolves_to_a_missing_key_marker()
    {
        ChromeLabels labels = ChromeLabels.Default;

        string[] all =
        [
            labels.SidebarName, labels.CollapseSidebar, labels.ExpandSidebar, labels.NoGroupChat, labels.StripName,
            labels.StatusName, labels.CloseSheet, labels.SheetGrip, labels.SheetOpened("T"), labels.SheetClosed("T"),
        ];

        Assert.All(all, text => Assert.DoesNotContain("<", text));
        Assert.Equal("en", labels.Locale);
        Assert.Equal("Collapse sidebar", labels.CollapseSidebar);
    }

    [Fact]
    public void The_sheet_messages_fill_the_title_argument_and_keep_it_verbatim()
    {
        Assert.Equal("Settings, dialog opened", ChromeLabels.Default.SheetOpened("Settings"));
        Assert.Equal("Settings closed", ChromeLabels.Default.SheetClosed("Settings"));

        // A title with braces or an apostrophe is data, not message syntax.
        Assert.Equal("{a} it's, dialog opened", ChromeLabels.Default.SheetOpened("{a} it's"));
    }

    [Fact]
    public void A_translated_catalogue_wins_and_missing_keys_fall_back_to_english()
    {
        ChromeLabels labels = ChromeLabels.For("de-AT", [German()]);

        Assert.Equal("Seitenleiste einklappen", labels.CollapseSidebar);
        Assert.Equal("Menü, Dialog geöffnet", labels.SheetOpened("Menü"));
        Assert.Equal(ChromeLabels.Default.CloseSheet, labels.CloseSheet);
    }

    [Fact]
    public void An_unrelated_locale_gets_english()
    {
        Assert.Equal(ChromeLabels.Default.SidebarName, ChromeLabels.For("fr", [German()]).SidebarName);
    }

    [Fact]
    public void The_label_source_rebuilds_labels_and_raises_changed_on_a_locale_change()
    {
        var locale = new SystemLocaleSource("en");
        using var source = new ChromeLabelSource(locale, [German()]);
        int raised = 0;
        source.Changed += (_, _) => raised++;

        Assert.Equal("Collapse sidebar", source.Labels.CollapseSidebar);

        locale.Set("de");

        Assert.Equal(1, raised);
        Assert.Equal("Seitenleiste einklappen", source.Labels.CollapseSidebar);
    }

    [Fact]
    public void A_disposed_label_source_stops_following()
    {
        var locale = new SystemLocaleSource("en");
        var source = new ChromeLabelSource(locale, [German()]);
        source.Dispose();

        locale.Set("de");

        Assert.Equal("en", source.Labels.Locale);
    }

    [AvaloniaFact]
    public void A_locale_switch_renames_the_sidebar_toggle_and_the_sidebar()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        var locale = new SystemLocaleSource("en");
        shell.Follow(new ChromeLabelSource(locale, [German()]));

        Assert.Equal("Collapse sidebar", AutomationProperties.GetName(shell.Sidebar.ToggleButton));

        locale.Set("de");

        Assert.Equal("Seitenleiste einklappen", AutomationProperties.GetName(shell.Sidebar.ToggleButton));
        Assert.Equal("Gruppenchats", AutomationProperties.GetName(shell.Sidebar.View));

        // The name still follows the state after the switch.
        shell.Sidebar.Toggle();
        Assert.Equal("Seitenleiste ausklappen", AutomationProperties.GetName(shell.Sidebar.ToggleButton));

        locale.Set("en");
        Assert.Equal("Expand sidebar", AutomationProperties.GetName(shell.Sidebar.ToggleButton));
    }

    [AvaloniaFact]
    public void A_locale_switch_changes_the_strip_placeholder_and_the_sheet_announcement()
    {
        var rig = new ChromeRig();
        DesktopChromeShell shell = rig.Create();
        var spoken = new List<string>();
        shell.Sheets.Announcer = new Recorder(spoken);
        var locale = new SystemLocaleSource("en");
        shell.Follow(new ChromeLabelSource(locale, [German()]));
        locale.Set("de");

        shell.Sheets.Show(new Avalonia.Controls.Button(), "Menü");

        Assert.Equal(["Menü, Dialog geöffnet"], spoken);
        Assert.Equal(ChromeLabels.Default.NoGroupChat, shell.Strip.TitleText);
    }

    private sealed class Recorder(List<string> sink) : ISheetAnnouncer
    {
        public void Announce(string text) => sink.Add(text);
    }
}
