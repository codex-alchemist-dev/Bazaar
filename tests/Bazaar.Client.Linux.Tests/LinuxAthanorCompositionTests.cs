using Avalonia.Headless.XUnit;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.AvaloniaEdit;
using Bazaar.Client.Athanor.Gtk;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Chat;

namespace Bazaar.Client.Linux.Tests;

public sealed class LinuxAthanorCompositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-linux-athanor-" + Guid.NewGuid().ToString("N"));

    public LinuxAthanorCompositionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AthanorHeadOptions Options(EditorHostKind host = EditorHostKind.Auto, string? helper = null) =>
        new() { DataDirectory = _root, Host = host, GtkHelperPath = helper };

    private string BundleWithPage()
    {
        var bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "host.html"), "<html></html>");
        return bundle;
    }

    private string Helper()
    {
        var path = Path.Combine(_root, "helper.sh");
        File.WriteAllText(path, "#!/bin/sh\nexit 3\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static AthanorEditorServices Create(AthanorHeadOptions options, string bundle, ChatConnectionSettings? server = null, List<string>? log = null) =>
        AthanorHeadComposition.Create(options, server, AthanorClientModule.Commands, line => log?.Add(line), bundle);

    [AvaloniaFact]
    public async Task Default_is_avaloniaedit_and_the_style_include_is_added()
    {
        await using var services = Create(Options(), BundleWithPage());

        Assert.Equal(EditorHostKind.AvaloniaEdit, services.Kind);
        Assert.IsType<AvaloniaEditHost>(services.Host);
        Assert.False(AvaloniaEditStyles.Include(Avalonia.Application.Current!));
        Assert.IsType<OfflineEditorFileSource>(services.Source);
    }

    [AvaloniaFact]
    public async Task Gtk_requested_without_a_helper_falls_back_and_says_why()
    {
        var log = new List<string>();
        await using var services = Create(Options(EditorHostKind.Gtk), BundleWithPage(), log: log);

        Assert.Equal(EditorHostKind.AvaloniaEdit, services.Kind);
        Assert.Contains(log, line => line.Contains("Gtk", StringComparison.Ordinal));
    }

    [Fact]
    public void Gtk_needs_the_helper_file_and_the_bundle_page()
    {
        var bundle = BundleWithPage();
        var helper = Helper();

        Assert.True(AthanorHeadComposition.GtkAvailable(Options(EditorHostKind.Gtk, helper), bundle));
        Assert.False(AthanorHeadComposition.GtkAvailable(Options(EditorHostKind.Gtk, null), bundle));
        Assert.False(AthanorHeadComposition.GtkAvailable(Options(EditorHostKind.Gtk, Path.Combine(_root, "missing")), bundle));
        Assert.False(AthanorHeadComposition.GtkAvailable(Options(EditorHostKind.Gtk, helper), Path.Combine(_root, "no-bundle")));
    }

    [AvaloniaFact]
    public async Task The_gtk_helper_is_used_only_when_selected_and_available()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The helper script needs a Unix shell.");
        var bundle = BundleWithPage();
        var helper = Helper();

        await using var auto = Create(Options(EditorHostKind.Auto, helper), bundle);
        Assert.Equal(EditorHostKind.AvaloniaEdit, auto.Kind);

        var log = new List<string>();
        await using var gtk = Create(Options(EditorHostKind.Gtk, helper), bundle, log: log);
        Assert.Equal(EditorHostKind.Gtk, gtk.Kind);
        Assert.IsType<GtkEditorHost>(gtk.Host);
    }

    [AvaloniaFact]
    public async Task A_server_connection_selects_the_wire_source()
    {
        var server = new ChatConnectionSettings(new Uri("ws://127.0.0.1:9/ws"), "token", Guid.NewGuid(), "me");

        await using var services = Create(Options(), BundleWithPage(), server);

        Assert.IsNotType<OfflineEditorFileSource>(services.Source);
    }
}
