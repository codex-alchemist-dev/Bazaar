using Bazaar.Client.Commands;

namespace Bazaar.Client.Panes.Abstractions;

/// <summary>What a pane module calls from its <c>Register</c> to make a command open its pane.</summary>
public static class PaneCommandBindings
{
    /// <summary>
    /// Makes <paramref name="commandId"/> bring the pane of <paramref name="paneKind"/> into view. Does nothing when the head
    /// gave no <see cref="ICommandRouter"/> (a head without keyboard mnemonics). The navigator is looked up when the command
    /// runs, and a layout that cannot show the pane is reported through the context's report callback.
    /// </summary>
    public static void Open(PaneModuleContext context, string commandId, string paneKind)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paneKind);

        var router = context.Get<ICommandRouter>();
        if (router is null)
        {
            return;
        }

        router.Register(commandId, () =>
        {
            if (context.Get<IPaneNavigator>() is not { } navigator || !navigator.Show(paneKind))
            {
                context.Report?.Invoke($"Command '{commandId}' could not show pane '{paneKind}'.");
            }
        });
    }
}
