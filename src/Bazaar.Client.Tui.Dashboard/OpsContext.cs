using Bazaar.Server.WebAdmin;

namespace Bazaar.Client.Tui.Dashboard;

/// <summary>Everything a screen needs to draw itself: the size it has, the look, and the clock (so tests are exact).</summary>
public sealed record ScreenContext(int Width, int Rows, TuiStyle Style, TimeZoneInfo Zone, DateTimeOffset Now);

/// <summary>
/// The ports the Ops screens read. Any may be null: a screen whose port is missing, or whose call throws, shows "unavailable" and the
/// other screens keep working (doc 51 section 5, soft edges). Only <c>.Abstractions</c> types appear here; never Bazaar.Server.
/// </summary>
public sealed record OpsPorts(
    IAdminStatusSource? Status = null,
    IAdminKodoSource? Kodo = null,
    IOpsEventSource? Events = null,
    IOpsCrashJournal? Crashes = null,
    IOpsLogSource? Logs = null,
    IAdminUserDirectory? Users = null,
    IAdminDeviceDirectory? Devices = null,
    IAdminAuditReader? Audit = null,
    IAdminApprovalSource? Approvals = null,
    IAdminModuleSelection? Modules = null);

/// <summary>Small helpers for colour that is only ever an addition to text (doc 58 section 4).</summary>
public static class Ansi
{
    private const string _reset = "\u001b[0m";

    public static string Green(TuiStyle style, string text) => style.UseColor ? "\u001b[32m" + text + _reset : text;

    public static string Reverse(TuiStyle style, string text) => style.UseColor ? "\u001b[7m" + text + _reset : text;

    public static string Underline(TuiStyle style, string text) => style.UseColor ? "\u001b[4m" + text + _reset : text;

    /// <summary>The severity word in brackets, coloured when colour is on. The word is always there.</summary>
    public static string Tag(TuiStyle style, OpsSeverity severity)
    {
        var word = "[" + KodoSeverity.Label(severity) + "]";
        return severity switch
        {
            OpsSeverity.Serious => style.Error(style.Bold(word)),
            OpsSeverity.Minor => style.Warning(word),
            _ => word,
        };
    }
}

/// <summary>Reads a port without letting its failure take the screen down. Null means "unavailable" (no port, or the call threw).</summary>
internal static class SoftPort
{
    public static async Task<T?> TryAsync<TPort, T>(TPort? port, Func<TPort, CancellationToken, Task<T>> call, CancellationToken ct)
        where TPort : class
        where T : class
    {
        if (port is null)
        {
            return null;
        }

        try
        {
            return await call(port, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
