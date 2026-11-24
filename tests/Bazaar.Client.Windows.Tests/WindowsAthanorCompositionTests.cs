using Avalonia.Headless.XUnit;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.AvaloniaEdit;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Chat;

namespace Bazaar.Client.Windows.Tests;

public sealed class WindowsAthanorCompositionTests
{
    private static AthanorHeadOptions Options(EditorHostKind host = EditorHostKind.Auto) =>
        new() { DataDirectory = Path.Combine(Path.GetTempPath(), "bazaar-athanor-tests"), Host = host };

    private static AthanorEditorServices Create(AthanorHeadOptions options, ChatConnectionSettings? server = null, bool webView2 = false, List<string>? log = null) =>
        AthanorHeadComposition.Create(options, server, AthanorClientModule.Commands, line => log?.Add(line), () => webView2);

    [AvaloniaFact]
    public async Task Without_the_webview2_runtime_the_editor_is_avaloniaedit_and_the_reason_is_logged()
    {
        var log = new List<string>();
        await using var services = Create(Options(), log: log);

        Assert.Equal(EditorHostKind.AvaloniaEdit, services.Kind);
        Assert.IsType<AvaloniaEditHost>(services.Host);
        Assert.Contains(log, line => line.Contains("WebView2", StringComparison.Ordinal));
        Assert.Contains(services.Notes, n => n.StartsWith("WebView2", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task The_operator_can_ask_for_the_plain_text_host()
    {
        await using var services = Create(Options(EditorHostKind.PlainText));

        Assert.Equal(EditorHostKind.PlainText, services.Kind);
        Assert.IsType<PlainTextFallbackHost>(services.Host);
    }

    [AvaloniaFact]
    public async Task A_host_the_head_does_not_offer_is_skipped()
    {
        var log = new List<string>();
        await using var services = Create(Options(EditorHostKind.Gtk), log: log);

        Assert.Equal(EditorHostKind.AvaloniaEdit, services.Kind);
        Assert.Contains(log, line => line.Contains("Gtk", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Offline_without_a_server_and_wire_with_one()
    {
        await using var offline = Create(Options(EditorHostKind.PlainText));
        Assert.IsType<OfflineEditorFileSource>(offline.Source);

        var server = new ChatConnectionSettings(new Uri("ws://127.0.0.1:9/ws"), "token", Guid.NewGuid(), "me");
        await using var online = Create(Options(EditorHostKind.PlainText), server);
        Assert.IsNotType<OfflineEditorFileSource>(online.Source);
    }

    [AvaloniaFact]
    public async Task Drafts_live_under_the_configured_data_folder_and_the_default_is_local_app_data()
    {
        await using var services = Create(Options(EditorHostKind.PlainText));
        Assert.NotNull(services.Drafts);

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AthanorHeadComposition.DefaultDataDirectory(),
            StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Options().DataDirectory, "athanor-drafts"), Options().DraftsDirectory);
    }

    [AvaloniaFact]
    public async Task The_pane_gets_the_composed_services()
    {
        await using var services = Create(Options(EditorHostKind.PlainText));

        var context = Program.CreateFeatures(tiles: null, services);

        Assert.Same(services.Host, context.Get<IEditorHost>());
        Assert.Same(services.Source, context.Get<IEditorFileSource>());
        Assert.Same(services.Drafts, context.Get<IEditorDraftStore>());
    }

    [Fact]
    public void Default_wiring_is_still_the_plain_host_over_the_offline_source()
    {
        var context = Program.CreateFeatures(tiles: null);

        Assert.IsType<PlainTextFallbackHost>(context.Get<IEditorHost>());
        Assert.IsType<OfflineEditorFileSource>(context.Get<IEditorFileSource>());
    }
}
