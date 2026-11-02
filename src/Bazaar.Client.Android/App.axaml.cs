using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bazaar.Client.Core.Mobile;

namespace Bazaar.Client.Android;

public sealed class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // TODO(android-shell): the activity lifetime re-creates the view on configuration change; posture is read from WindowManager (doc 29).
        if (ApplicationLifetime is IActivityApplicationLifetime activity)
        {
            activity.MainViewFactory = () => new PhoneShell();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new PhoneShell();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
