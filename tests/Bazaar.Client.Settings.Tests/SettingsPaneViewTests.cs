using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;
using static Bazaar.Client.Settings.Tests.SettingsTestSupport;

namespace Bazaar.Client.Settings.Tests;

public class SettingsPaneViewTests
{
    private static PaneDescriptor Descriptor() => new(PaneKinds.Settings, "Settings", string.Empty, JsonElement.Parse("{}"));

    private static async Task<SettingsPaneViewModel> LoadedAsync(Bazaar.Server.Settings.SettingActor actor)
    {
        var pane = new SettingsPaneViewModel(Descriptor(), new FakeClient(Snapshot(actor)));
        await pane.LoadAsync(TestContext.Current.CancellationToken);
        return pane;
    }

    [Fact]
    public void Module_registers_the_settings_kind_and_the_nav_command()
    {
        var registry = new PaneRegistry(new PlainViews());
        var module = new SettingsPaneModule();
        module.Register(registry, new PaneModuleContext());

        Assert.Equal([PaneKinds.Settings], registry.Kinds);
        Assert.IsType<SettingsPaneViewModel>(registry.Create(Descriptor()));
        Assert.Contains("nav.settings", module.Manifest.Commands);
        Assert.Equal("settings", SettingsPaneModule.Commands[0].Mnemonic);
    }

    [Fact]
    public async Task Module_uses_the_client_the_head_hands_over()
    {
        var registry = new PaneRegistry(new PlainViews());
        new SettingsPaneModule().Register(registry, new PaneModuleContext().With<ISettingsClient>(new FakeClient(Snapshot(Operator))));

        var pane = Assert.IsType<SettingsPaneViewModel>(registry.Create(Descriptor()));
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(pane.IsConnected);
        Assert.Equal(6, pane.Rows.Count);
    }

    [AvaloniaFact]
    public async Task View_renders_groups_rows_editors_and_automation_names()
    {
        var pane = await LoadedAsync(Operator);

        var view = SettingsPaneView.Create(pane);
        var all = view.GetLogicalDescendants().ToArray();
        var texts = all.OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToArray();

        Assert.Contains("backup", texts);
        Assert.Contains("host.bind", texts);
        Assert.Contains(texts, t => t.StartsWith("A change can need Tier2 approval", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Read-only: only Owner may change this", StringComparison.Ordinal));
        Assert.Contains(all.OfType<ToggleSwitch>(), _ => true);
        Assert.Contains(all.OfType<ComboBox>(), _ => true);

        var names = all.OfType<Control>().Select(AutomationProperties.GetName).ToArray();
        Assert.Contains("Search settings", names);
        Assert.Contains("backup.days value", names);
        Assert.Contains("Save backup.days", names);
    }

    [AvaloniaFact]
    public async Task Read_only_row_has_a_disabled_editor()
    {
        var pane = await LoadedAsync(Operator);

        var view = SettingsPaneView.Create(pane);
        var nameEditor = view.GetLogicalDescendants().OfType<Control>().Single(c => AutomationProperties.GetName(c) == "mail.name value");

        Assert.False(nameEditor.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Typing_a_bad_value_shows_the_registry_error_in_the_row()
    {
        var pane = await LoadedAsync(Operator);

        var view = SettingsPaneView.Create(pane);
        var editor = view.GetLogicalDescendants().OfType<TextBox>().Single(c => AutomationProperties.GetName(c) == "backup.days value");
        editor.Text = "400";

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToArray();
        Assert.Contains("Days must be 1 to 90.", texts);
        var save = view.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Save backup.days");
        Assert.False(save.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Not_connected_pane_shows_the_status_line()
    {
        var pane = new SettingsPaneViewModel(Descriptor(), NotConnectedSettingsClient.Instance);
        await pane.LoadAsync(TestContext.Current.CancellationToken);

        var view = SettingsPaneView.Create(pane);

        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == SettingsPaneViewModel.NotConnectedText);
    }

    private sealed class PlainViews : IPaneViewFactory
    {
        public Control Create(IPaneViewModel pane) => new TextBlock { Text = pane.Kind };

        public Control WithTitle(string title, Control body) =>
            new StackPanel { Children = { new TextBlock { Text = title }, body } };
    }
}
