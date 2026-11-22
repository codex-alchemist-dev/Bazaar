using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Ex.Head;

namespace Bazaar.Client.Windows.Tests;

public sealed class WindowsAthanorCommandKeyTests
{
    private static string DataDir() => Path.Combine(Path.GetTempPath(), "bazaar-athanor-cmd-tests", Guid.NewGuid().ToString("N"));

    private static void Type(Window window, string text)
    {
        foreach (var c in text)
        {
            window.KeyPress(Enum.Parse<Key>(char.ToUpperInvariant(c).ToString()), RawInputModifiers.None, PhysicalKey.None, c.ToString());
        }

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
    }

    private static (AthanorPaneModule Module, InMemoryEditorFileSource Source, AthanorPaneViewModel Pane) Compose()
    {
        var source = new InMemoryEditorFileSource();
        var context = new PaneModuleContext()
            .With<IEditorHost>(new PlainTextFallbackHost())
            .With<IEditorFileSource>(source)
            .With<IEditorDraftStore>(new InMemoryEditorDraftStore());
        var module = new AthanorPaneModule();
        var registry = PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [module], context);
        var pane = Assert.IsType<AthanorPaneViewModel>(registry.Create(AthanorPaneModule.WindowPane));
        return (module, source, pane);
    }

    [AvaloniaFact]
    public async Task The_edsave_mnemonic_then_Enter_saves_the_active_document_without_a_Ctrl_chord()
    {
        var (module, source, pane) = Compose();
        var id = new EditorDocumentId("w", "a.txt");
        source.Seed(id, "one", "plaintext");
        Assert.True(await pane.OpenAsync(id, TestContext.Current.CancellationToken));
        pane.EditActive("two");
        var window = new Window { Content = new Border() };
        window.Show();
        var options = new AthanorHeadOptions { DataDirectory = DataDir() };
        using var commands = AthanorHeadComposition.StartCommands(window, AthanorClientModule.Commands, module, options, _ => { });
        Assert.NotNull(commands);

        Type(window, "edsave");
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var saved = await source.OpenAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal("two", saved!.Content);
    }

    [AvaloniaFact]
    public async Task The_other_athanor_mnemonics_reach_the_handler_and_report_what_they_cannot_do()
    {
        var (module, _, _) = Compose();
        var window = new Window { Content = new Border() };
        window.Show();
        var log = new List<string>();
        var options = new AthanorHeadOptions { DataDirectory = DataDir() };
        using var commands = AthanorHeadComposition.StartCommands(window, AthanorClientModule.Commands, module, options, log.Add);

        Type(window, "eddiff");
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(log, l => l.Contains(AthanorClientModule.DiffCommandId, StringComparison.Ordinal) && l.Contains("No document is open", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_edopen_mnemonic_shows_a_prompt_and_opens_the_typed_path_and_a_cancelled_prompt_does_nothing()
    {
        var (module, source, pane) = Compose();
        var id = new EditorDocumentId("w", "a.txt");
        source.Seed(id, "one", "plaintext");
        var window = new Window { Content = new Border() };
        window.Show();
        var options = new AthanorHeadOptions { DataDirectory = DataDir() };
        using var commands = AthanorHeadComposition.StartCommands(window, AthanorClientModule.Commands, module, options, _ => { });
        var layer = OverlayLayer.GetOverlayLayer(window);
        Assert.NotNull(layer);

        Type(window, "edopen");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        layer.Children.OfType<EditorPromptView>().Single().Cancel();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(pane.Tabs);
        Assert.Empty(layer.Children.OfType<EditorPromptView>());

        Type(window, "edopen");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var prompt = layer.Children.OfType<EditorPromptView>().Single();
        prompt.Text = "w:a.txt";
        prompt.Submit();
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(id, Assert.Single(pane.Tabs).Id);
    }

    [AvaloniaFact]
    public void The_head_host_uses_the_palette_text_the_claim_table_assumes()
    {
        Assert.Equal(EditorRouterTable.PlaceholderPaletteMnemonic, MnemonicHeadHost.PaletteMnemonic);
    }
}
