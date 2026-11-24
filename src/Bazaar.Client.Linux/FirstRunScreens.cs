using Avalonia.Controls;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.CompanySetup;
using Bazaar.Client.CompanySetup.Avalonia;
using Bazaar.Client.SignIn;
using Bazaar.Client.SignIn.Avalonia;
using Bazaar.Contracts;

namespace Bazaar.Client.Linux;

/// <summary>
/// Builds the sign-in and create-company screens (doc 45 section 2.6) for the <c>--sign-in</c>, <c>--create-company</c> and
/// <c>--first-run</c> switches. Both talk to the server this build was made for (<see cref="ClientServer.Current"/>); the session
/// is kept sealed by <see cref="LinuxClientSessions"/> (keyring key, or a 0600 key file), so a restart stays signed in.
/// </summary>
internal static class FirstRunScreens
{
    public const string SignInSwitch = "--sign-in";
    public const string CreateCompanySwitch = "--create-company";
    public const string FirstRunSwitch = "--first-run";

    /// <summary>Which screens the arguments ask for.</summary>
    internal static (bool SignIn, bool Company) Parse(IReadOnlyList<string> args)
    {
        var both = args.Contains(FirstRunSwitch, StringComparer.Ordinal);
        return (both || args.Contains(SignInSwitch, StringComparer.Ordinal), both || args.Contains(CreateCompanySwitch, StringComparer.Ordinal));
    }

    /// <summary>The screens the arguments ask for, or null when none is asked for.</summary>
    public static Control? TryBuild(IReadOnlyList<string> args)
    {
        var (signIn, company) = Parse(args);
        if (!signIn && !company) return null;

        var http = ClientServer.Current.CreateHttpClient();
        var store = LinuxClientSessions.Store;
        var panel = new StackPanel { Spacing = 24, Margin = new Avalonia.Thickness(16) };

        if (signIn)
        {
            var model = new SignInViewModel(new HttpSignInService(http, store, "linux", ProductVersion.Informational), new XdgOpenUrlLauncher());
            panel.Children.Add(SignInView.Create(model));
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = model.StartAsync());
        }

        if (company)
        {
            var model = new CompanySetupViewModel(
                new HttpCompanySetupService(http),
                new LinuxKeyCustodyDefaults(),
                async () => (await store.LoadAsync())?.AccessToken);
            panel.Children.Add(CompanySetupView.Create(model));
        }

        return new ScrollViewer { Content = panel };
    }

    /// <summary>
    /// Doc 14 (<c>identity.kek_provider</c>, DECIDED 2026-11-16): a protected key file on hosts that are not Windows. Doc 45's
    /// table names systemd-creds for Linux, but that is a reference scheme, not a provider value; doc 14 owns the enum, so the
    /// screen proposes <c>file</c> and leaves the reference for the person to give.
    /// </summary>
    private sealed class LinuxKeyCustodyDefaults : IKeyCustodyDefaults
    {
        public string? DefaultProvider => "file";

        public string DefaultReference => "";
    }
}
