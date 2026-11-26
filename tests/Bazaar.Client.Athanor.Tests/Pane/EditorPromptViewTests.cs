using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Bazaar.Client.Athanor.Pane;

namespace Bazaar.Client.Athanor.Tests.Pane;

public sealed class EditorPromptViewTests
{
    [AvaloniaFact]
    public async Task Submit_returns_the_trimmed_text_and_an_empty_text_counts_as_cancelled()
    {
        var view = new EditorPromptView("Open document", "path");
        var window = new Window { Content = view };
        window.Show();

        view.Text = "  src/a.cs ";
        view.Submit();
        var result = await view.Result;
        Assert.Equal("src/a.cs", result?.Text);
        Assert.Null(result?.LanguageId);

        var blank = new EditorPromptView("Open document", "path");
        blank.Submit();
        Assert.Null(await blank.Result);
    }

    [AvaloniaFact]
    public async Task Escape_cancels()
    {
        var view = new EditorPromptView("Open document", "path");
        var window = new Window { Content = view };
        window.Show();
        view.FocusInput();

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Null(await view.Result);
    }

    [AvaloniaFact]
    public async Task Language_follows_the_typed_name_until_the_user_picks_one()
    {
        var view = new EditorPromptView("New document", "name", [LanguageIds.PlainText, LanguageIds.CSharp, LanguageIds.Json]);
        var window = new Window { Content = view };
        window.Show();

        Assert.Equal(LanguageIds.PlainText, view.SelectedLanguage);
        view.Text = "src/Thing.cs";
        Assert.Equal(LanguageIds.CSharp, view.SelectedLanguage);

        view.SelectedLanguage = LanguageIds.Json;
        view.Text = "src/Other.cs";
        Assert.Equal(LanguageIds.Json, view.SelectedLanguage);

        view.Submit();
        var result = await view.Result;
        Assert.Equal(new EditorPromptResult("src/Other.cs", LanguageIds.Json), result);
    }

    [AvaloniaFact]
    public async Task Prompts_mount_on_the_overlay_layer_and_a_cancelled_one_returns_null()
    {
        var window = new Window();
        window.Show();
        var prompts = new AvaloniaEditorCommandPrompts(window);
        var layer = OverlayLayer.GetOverlayLayer(window);
        Assert.NotNull(layer);

        var pending = prompts.AskOpenPathAsync(TestContext.Current.CancellationToken);
        var view = Assert.Single(layer.Children.OfType<EditorPromptView>());
        view.Text = "ws:src/a.cs";
        view.Submit();
        Assert.Equal("ws:src/a.cs", await pending);
        Assert.Empty(layer.Children.OfType<EditorPromptView>());

        var asked = prompts.AskNewDocumentAsync(TestContext.Current.CancellationToken);
        layer.Children.OfType<EditorPromptView>().Single().Cancel();
        Assert.Null(await asked);
        Assert.Empty(layer.Children.OfType<EditorPromptView>());
    }
}
