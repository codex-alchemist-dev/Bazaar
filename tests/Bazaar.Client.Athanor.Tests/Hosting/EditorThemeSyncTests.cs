using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Tests.Workspace;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Athanor.Tests.Hosting;

public class EditorThemeSyncTests
{
    [Fact]
    public async Task Start_pushes_the_current_theme_as_the_monaco_json()
    {
        var service = new ClientThemeService(HostingFixtures.DarkTheme());
        var host = new FakeEditorHost();
        using var sync = new EditorThemeSync(new ClientThemeEditorSource(service), () => host, _ => { });

        await sync.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal([MonacoThemeGenerator.Generate(service.Current)], host.Themes);
    }

    [Fact]
    public async Task A_theme_change_is_pushed_live_and_after_dispose_it_is_not()
    {
        var service = new ClientThemeService(HostingFixtures.DarkTheme("#89B4FA"));
        var host = new FakeEditorHost();
        var sync = new EditorThemeSync(new ClientThemeEditorSource(service), () => host, _ => { });
        await sync.StartAsync(TestContext.Current.CancellationToken);

        var swapped = HostingFixtures.DarkTheme("#F38BA8");
        service.Set(swapped);
        await WaitForAsync(() => host.Themes.Count == 2);

        Assert.Equal(MonacoThemeGenerator.Generate(swapped), host.Themes[1]);

        sync.Dispose();
        service.Set(HostingFixtures.DarkTheme("#A6E3A1"));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(2, host.Themes.Count);
    }

    [Fact]
    public async Task A_failing_host_is_logged_and_never_thrown()
    {
        var service = new ClientThemeService(HostingFixtures.DarkTheme());
        var log = new List<string>();
        using var sync = new EditorThemeSync(new ClientThemeEditorSource(service), () => new ThrowingHost(), log.Add);

        await sync.StartAsync(TestContext.Current.CancellationToken);

        Assert.Single(log);
        Assert.Contains("not applied", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_active_host_means_nothing_is_pushed_and_nothing_is_logged()
    {
        var service = new ClientThemeService(HostingFixtures.DarkTheme());
        var log = new List<string>();
        using var sync = new EditorThemeSync(new ClientThemeEditorSource(service), () => null, log.Add);

        await sync.StartAsync(TestContext.Current.CancellationToken);

        Assert.Empty(log);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class ThrowingHost : IEditorHost
    {
        public event EventHandler<EditorChange>? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler<EditorDocumentId>? SaveRequested
        {
            add { }
            remove { }
        }

        public event EventHandler<EditorKeyEvent>? KeyPressed
        {
            add { }
            remove { }
        }

        public event EventHandler? Ready
        {
            add { }
            remove { }
        }

        public event EventHandler<EditorCrashReason>? Crashed
        {
            add { }
            remove { }
        }

        public Task OpenAsync(EditorDocument document, CancellationToken cancellationToken) => throw Boom();

        public Task CloseAsync(EditorDocumentId id, CancellationToken cancellationToken) => throw Boom();

        public Task FocusAsync(CancellationToken cancellationToken) => throw Boom();

        public Task SetThemeAsync(string monacoThemeJson, CancellationToken cancellationToken) => throw Boom();

        public Task SetReadOnlyAsync(EditorDocumentId id, bool isReadOnly, CancellationToken cancellationToken) => throw Boom();

        public Task ShowDiffAsync(string original, string modified, string languageId, CancellationToken cancellationToken) => throw Boom();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static InvalidOperationException Boom() => new("host is gone");
    }
}
