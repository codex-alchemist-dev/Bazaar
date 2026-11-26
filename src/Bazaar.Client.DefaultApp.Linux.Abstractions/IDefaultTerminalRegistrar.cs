namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>
/// Offers Bazaar's terminal as the user's default terminal emulator on Linux (doc 30 section 1, "Default terminal"). The only
/// per-user mechanism that exists is the <c>xdg-terminals.list</c> file of the xdg-terminal-exec specification, so that is all
/// <see cref="RegisterAsync"/> writes. It never claims a silent default: other launchers (Debian <c>x-terminal-emulator</c>,
/// GNOME, KDE) keep their own settings, and the result says so with the command or page the user needs.
/// </summary>
public interface IDefaultTerminalRegistrar
{
    /// <summary>Writes Bazaar's terminal desktop entry and puts it first in the user's <c>xdg-terminals.list</c>, then reads the result back.</summary>
    Task<DefaultAppStatus> RegisterAsync(CancellationToken ct);

    /// <summary>Reports which terminal the xdg-terminal-exec configuration names first, plus what GNOME and KDE hold, without changing anything.</summary>
    Task<DefaultAppStatus> QueryAsync(CancellationToken ct);

    /// <summary>Removes Bazaar's lines from the user's <c>xdg-terminals.list</c> and its desktop entry (only one this module wrote). Safe to repeat.</summary>
    Task<DefaultAppStatus> UnregisterAsync(CancellationToken ct);
}
