namespace Bazaar.Client.DefaultApp.Linux;

/// <summary>
/// Registers Bazaar as a handler on Linux and reports honestly whether it is the default. It never claims a silent default:
/// when the desktop does not agree, the result is <see cref="DefaultAppState.NotDefault"/> with guidance.
/// </summary>
public interface IDefaultAppRegistrar
{
    /// <summary>Writes or updates the per-user desktop entry, asks <c>xdg-mime</c> to make it the default, and reads the result back.</summary>
    Task<DefaultAppStatus> RegisterAsync(CancellationToken ct);

    /// <summary>Reads the current default for every configured type without changing anything.</summary>
    Task<DefaultAppStatus> QueryAsync(CancellationToken ct);

    /// <summary>Removes the per-user desktop entry, but only one this module wrote. Safe to repeat.</summary>
    Task<DefaultAppStatus> UnregisterAsync(CancellationToken ct);
}
