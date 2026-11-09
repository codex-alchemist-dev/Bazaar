using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Android.Pairing;
using Bazaar.Client.Core.Mobile;
using Bazaar.Client.Core.Pairing;
using Bazaar.Client.Core.Server;

namespace Bazaar.Client.Android;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The first screen is the pairing screen (doc 32, section 3). On success the paired endpoint is set and the phone shell
        // replaces it. The host stays the root view, so both lifetimes keep one stable root.
        // TODO(android-shell): the activity lifetime re-creates the view on configuration change; posture is read from WindowManager (doc 29).
        var host = new ContentControl();
        var flow = new PairingFlow(
            AndroidRefreshCredentials.Create(global::Android.App.Application.Context),
            uri => PairingClient.Create(uri));
        var pairing = new PairingViewModel(flow, deviceClass: "android");
        pairing.Paired += (_, completed) =>
        {
            ClientServer.Use(PairingClient.ToEndpoint(completed.BaseAddress, completed.Grant));
            host.Content = new PhoneShell();
        };
        host.Content = new PairingView(pairing);

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => host;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = host;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
