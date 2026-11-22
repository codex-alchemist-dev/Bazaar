using System.Diagnostics;
using Avalonia.Controls;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.CompanySetup;
using Bazaar.Client.CompanySetup.Avalonia;
using Bazaar.Client.SignIn;
using Bazaar.Client.SignIn.Avalonia;
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
        var store = ClientSessions.Store;
        var panel = new StackPanel { Spacing = 24, Margin = new Avalonia.Thickness(16) };

        if (signIn)
        {
            var model = new SignInViewModel(new HttpSignInService(http, store, "windows", ProductVersion.Informational), new ShellUrlLauncher());
            panel.Children.Add(SignInView.Create(model));
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = model.StartAsync());
        }

        if (company)
        {
            var model = new CompanySetupViewModel(
                new HttpCompanySetupService(http),
                new WindowsKeyCustodyDefaults(),
                async () => (await store.LoadAsync())?.AccessToken);
            panel.Children.Add(CompanySetupView.Create(model));
        }

        return new ScrollViewer { Content = panel };
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
