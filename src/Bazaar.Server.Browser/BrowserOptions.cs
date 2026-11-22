namespace Bazaar.Server.Browser;

/// <summary>
/// Operator settings for the browser module (configurability rule). Paths are inside the guest images. Defaults match
/// the golden image recipe's Chromium locations.
/// </summary>
public sealed record BrowserOptions(
    string LinuxExecutable = "/usr/bin/chromium",
    string WindowsExecutable = @"C:\Program Files\Chromium\Application\chrome.exe",
    string LinuxProfileRoot = "/var/lib/bazaar/browser",
    string WindowsProfileRoot = @"C:\ProgramData\Bazaar\browser",
    TimeSpan MaxSessionAge = default)
{
    public const string SectionName = "Bazaar:Browser";

    /// <summary>Longest a session may live before the sweeper ends it. Default 8 hours; must be positive.</summary>
    public TimeSpan EffectiveMaxSessionAge => MaxSessionAge > TimeSpan.Zero ? MaxSessionAge : TimeSpan.FromHours(8);
}
