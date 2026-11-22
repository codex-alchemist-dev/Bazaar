using System.Diagnostics;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.CompanySetup;
using Bazaar.Client.CompanySetup.Avalonia;
using Bazaar.Client.SignIn;
using Bazaar.Client.SignIn.Avalonia;
using Bazaar.Client.Walkthrough;
using Bazaar.Client.Walkthrough.Avalonia;
using Bazaar.Contracts;

namespace Bazaar.Client.Windows;

/// <summary>
/// Builds the sign-in and create-company screens (doc 45 section 2.6) for the <c>--sign-in</c>, <c>--create-company</c> and
/// <c>--first-run</c> switches. Both talk to the server this build was made for (<see cref="ClientServer.Current"/>); the session
/// lives in memory until a protected store exists, so a restart signs out.
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
        var store = new InMemorySessionStore();
        var panel = new StackPanel { Spacing = 24, Margin = new Avalonia.Thickness(16) };

        if (signIn)
        {
            var model = new SignInViewModel(new HttpSignInService(http, store, "windows", ProductVersion.Informational), new ShellUrlLauncher());
            panel.Children.Add(SignInView.Create(model));
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = model.StartAsync());
        }

        var facts = new LocalFacts();
        if (both)
        {
            // The walkthrough card sits above the screens it guides to. Facts come from this run's own screens until the server
            // reports them; progress is cached in a local file (doc 45 section 2.7).
            var walkthrough = new WalkthroughViewModel(new FileWalkthroughProgressStore(WalkthroughPath()), facts, new NoScreenActions());
            panel.Children.Insert(0, WalkthroughView.Create(walkthrough));
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

    /// <summary>The agent profile and task composer are not in this head yet, so those steps report that they cannot open.</summary>
    private sealed class NoScreenActions : IWalkthroughActions
    {
        public Task<bool> OpenAsync(WalkthroughStep which, CancellationToken cancellationToken = default) => Task.FromResult(false);
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
