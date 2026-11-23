using Bazaar.Client.Navigation.Abstractions;
using Bazaar.Client.Preferences.Abstractions;

namespace Bazaar.Client.Views;

public sealed partial class ViewActionRouter
{
    /// <summary>
    /// The usual head wiring (doc 41c, section 6): navigate and openPane go to the navigator, invoke to the command runner,
    /// and setSetting to the preference store, which accepts only a declared key and a valid value. A refused setting throws,
    /// so the mapper reports it.
    /// </summary>
    public static ViewActionRouter ForServices(IPaneNavigator navigator, ICommandRunner commands, IPreferenceStore preferences)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(preferences);

        return new ViewActionRouter(
            navigate: target => navigator.NavigateAsync(target),
            invoke: command => commands.RunAsync(command),
            openPane: kind => navigator.OpenPaneAsync(kind),
            setSetting: (key, value) =>
            {
                PreferenceResult result = preferences.TrySet(key, value);
                return result.Accepted
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException($"Setting '{key}' was refused: {result.Reason}"));
            });
    }
}
