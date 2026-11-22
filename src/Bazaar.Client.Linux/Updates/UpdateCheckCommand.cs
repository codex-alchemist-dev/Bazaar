using Avalonia.Controls;
using Avalonia.Threading;
using Bazaar.Client.Commands;

namespace Bazaar.Client.Linux.Updates;

/// <summary>
/// The "Check for updates" command (doc 30 section 7). <see cref="Command"/> is the palette entry; <see cref="RunAsync"/> is
/// what it runs: check now, then show the outcome in a small notice window. Never throws.
/// </summary>
public sealed class UpdateCheckCommand
{
    private readonly UpdateCoordinator _coordinator;
    private readonly Func<Window?> _owner;

    /// <summary>Creates the command. <paramref name="owner"/> gives the window the notice belongs to, or null.</summary>
    public UpdateCheckCommand(UpdateCoordinator coordinator, Func<Window?> owner)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(owner);
        _coordinator = coordinator;
        _owner = owner;
    }

    /// <summary>The palette entry. Local commands are registered with the head's other local commands.</summary>
    public static Command Command { get; } =
        new(UpdateCoordinator.CheckCommandId, "Check for updates", "check for updates", ["update", "upgrade", "version"], CommandSource.Local);

    /// <summary>Checks now and shows the result. Call from the UI thread.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var notice = await _coordinator.CheckNowAsync(ct).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => Show(notice));
    }

    private void Show(UpdateNotice notice)
    {
        var window = new Window
        {
            Title = "Check for updates",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.Content = new UpdateNoticeView(notice, window.Close);
        if (_owner() is { IsVisible: true } owner)
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }
}
