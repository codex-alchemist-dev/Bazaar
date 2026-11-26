using System.Diagnostics;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.CompanySetup;
using Bazaar.Client.CompanySetup.Avalonia;
using Bazaar.Client.SignIn;
using Bazaar.Client.SignIn.Avalonia;
using Bazaar.Client.FirstTask;
using Bazaar.Client.FirstTask.Avalonia;
using Bazaar.Client.SignIn;
using Bazaar.Client.SignIn.Avalonia;
using Bazaar.Client.Souls;
using Bazaar.Client.Walkthrough;
using Bazaar.Client.Walkthrough.Avalonia;
using Bazaar.Contracts;

namespace Bazaar.Client.Windows;

/// <summary>
/// Builds the sign-in and create-company screens (doc 45 section 2.6) for the <c>--sign-in</c>, <c>--create-company</c> and
/// <c>--first-run</c> switches. Both talk to the server this build was made for (<see cref="ClientServer.Current"/>); the session
/// is kept sealed with DPAPI under the person's local application data, so a restart stays signed in.
/// </summary>
internal static class FirstRunScreens
{
    public const string SignInSwitch = "--sign-in";
    public const string CreateCompanySwitch = "--create-company";
    public const string FirstRunSwitch = "--first-run";

    /// <summary>The screens the arguments ask for, or null when none is asked for.</summary>
    public static Control? TryBuild(IReadOnlyList<string> args)
    {
        var both = args.Contains(FirstRunSwitch, StringComparer.Ordinal);
        var signIn = both || args.Contains(SignInSwitch, StringComparer.Ordinal);
        var company = both || args.Contains(CreateCompanySwitch, StringComparer.Ordinal);
        if (!signIn && !company) return null;

        var http = ClientServer.Current.CreateHttpClient();
        var store = SessionStores.Create();
        var store = ClientSessions.Store;
        var panel = new StackPanel { Spacing = 24, Margin = new Avalonia.Thickness(16) };

        if (signIn)
        {
            var model = new SignInViewModel(new HttpSignInService(http, store, "windows", ProductVersion.Informational), new ShellUrlLauncher());
            panel.Children.Add(SignInView.Create(model));
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = model.StartAsync());
        }

        var facts = new LocalFacts();
        var server = new HttpWalkthroughService(http, async () => (await store.LoadAsync())?.AccessToken);
        var composerHost = new StackPanel { IsVisible = false };
        var actions = new ScreenActions(composerHost);
        if (both)
        {
            // The walkthrough card sits above the screens it guides to. Facts come from the server, or from this run's own screens when it cannot say; progress is cached in a local file (doc 45 section 2.9).
            var walkthrough = new WalkthroughViewModel(
                new CachedWalkthroughProgressStore(server, new FileWalkthroughProgressStore(WalkthroughPath())),
                new FallbackFacts(server, facts),
                actions);
            panel.Children.Insert(0, WalkthroughView.Create(walkthrough));

            // The composer opens from the walkthrough's last real step and posts to the first agent's chat over a short wire connection.
            var composer = new FirstTaskViewModel(
                WireFirstTaskSender.For(ClientServer.Current, async () => (await store.LoadAsync())?.AccessToken, "windows", ProductVersion.Informational),
                new FactsTargetSource(new FallbackFacts(server, facts)));
            composerHost.Children.Add(FirstTaskView.Create(composer));
            actions.OnComposerOpened = () => _ = composer.StartAsync();

            // The agent's profile card: the first agent's id comes from the facts, the card from the read-only profile route (doc 07).
            var profiles = new HttpSoulProfileClient(http, async () => (await store.LoadAsync())?.AccessToken);
            var profileHost = new StackPanel { IsVisible = false };
            actions.OpenProfileAsync = async () =>
            {
                try
                {
                    var known = await new FallbackFacts(server, facts).ReadAsync();
                    if (known.FirstAgentId is not { } agentId || await profiles.GetAsync(agentId) is not { } agent) return false;
                    var view = new SoulProfileView { DataContext = new SoulProfileViewModel(agent.Profile, agent.Versions) };
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        profileHost.Children.Clear();
                        profileHost.Children.Add(view);
                        profileHost.IsVisible = true;
                    });
                    return true;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    return false;
                }
            };
            panel.Children.Insert(1, profileHost);
            panel.Children.Insert(1, composerHost);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = walkthrough.StartAsync());
        }

        if (company)
        {
            var model = new CompanySetupViewModel(
                new HttpCompanySetupService(http),
                new WindowsKeyCustodyDefaults(),
                async () => (await store.LoadAsync())?.AccessToken);
            model.Completed += (_, _) => facts.Set(new WalkthroughFacts(true, string.IsNullOrWhiteSpace(model.AgentName) ? null : model.AgentName, false));
            panel.Children.Add(CompanySetupView.Create(model));
        }

        return new ScrollViewer { Content = panel };
    }

    private static string WalkthroughPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", "walkthrough.json");

    /// <summary>What this run's screens have seen done. A server-backed source replaces it when the routes exist.</summary>
    private sealed class LocalFacts : IWalkthroughFactsSource
    {
        private WalkthroughFacts _facts = WalkthroughFacts.None;

        public void Set(WalkthroughFacts facts) => _facts = facts;

        public Task<WalkthroughFacts> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_facts);
    }

    /// <summary>Asks the server first; when it cannot answer (routes not built yet, server down) the run's own screens answer.</summary>
    private sealed class FallbackFacts(IWalkthroughFactsSource first, IWalkthroughFactsSource second) : IWalkthroughFactsSource
    {
        public async Task<WalkthroughFacts> ReadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await first.ReadAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return await second.ReadAsync(cancellationToken);
            }
        }
    }

    /// <summary>Reads the composer's target from the walkthrough's facts: the first agent and the chat seeded with it.</summary>
    private sealed class FactsTargetSource(IWalkthroughFactsSource facts) : IFirstTaskTargetSource
    {
        public async Task<FirstTaskTarget?> ReadAsync(CancellationToken cancellationToken = default)
        {
            var read = await facts.ReadAsync(cancellationToken);
            return read is { FirstAgentId: { } agent, FirstChatId: { } chat } ? new FirstTaskTarget(agent, chat) : null;
        }
    }

    /// <summary>
    /// Opens the screens this head has: the create-company form is already on the page, and the task composer is shown on demand.
    /// The agent's profile card opens from the profile route, and reports that it cannot open when the server has none to show.
    /// </summary>
    private sealed class ScreenActions(Control composerHost) : IWalkthroughActions
    {
        public Action? OnComposerOpened { get; set; }

        public Func<Task<bool>>? OpenProfileAsync { get; set; }

        public Task<bool> OpenAsync(WalkthroughStep which, CancellationToken cancellationToken = default)
        {
            switch (which)
            {
                case WalkthroughStep.CreateCompany:
                    return Task.FromResult(true);
                case WalkthroughStep.FirstTask:
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        composerHost.IsVisible = true;
                        OnComposerOpened?.Invoke();
                    });
                    return Task.FromResult(true);
                case WalkthroughStep.MeetAgent:
                    return OpenProfileAsync?.Invoke() ?? Task.FromResult(false);
                default:
                    return Task.FromResult(false);
            }
        }
    }

    /// <summary>The session store: DPAPI-sealed file when Windows allows it, memory only when it does not (nothing is written in the clear).</summary>
    internal static class SessionStores
    {
        public static ISessionStore Create()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return new InMemorySessionStore();
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(root)
                    ? new InMemorySessionStore()
                    : new ProtectedFileSessionStore(Path.Combine(root, "Bazaar", "session.bin"), new DpapiSessionProtector());
            }
            catch (PlatformNotSupportedException)
            {
                return new InMemorySessionStore();
            }
        }
    }

    /// <summary>Opens an address in the person's default browser.</summary>
    private sealed class ShellUrlLauncher : IUrlLauncher
    {
        public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default)
        {
            try
            {
                // Only web addresses: the sign-in page is http or https and nothing else is ever launched.
                if (address.Scheme is not ("http" or "https")) return Task.FromResult(false);
                using var started = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
                return Task.FromResult(started is not null);
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return Task.FromResult(false);
            }
        }
    }

    /// <summary>
    /// Windows hosts seal the key-encryption key with DPAPI. The reference path is the server host's to decide (doc 03), so the
    /// screen proposes the provider and leaves the reference for the person to give.
    /// </summary>
    private sealed class WindowsKeyCustodyDefaults : IKeyCustodyDefaults
    {
        public string? DefaultProvider => "dpapi";

        public string DefaultReference => "";
    }
}
