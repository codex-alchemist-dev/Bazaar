namespace Bazaar.Server.Browser;

/// <summary>
/// Operator settings for the browser module (configurability rule). Paths are inside the guest images. Defaults match
/// the golden image recipe's Chromium locations.
/// </summary>
public sealed record BrowserOptions(
    string LinuxExecutable = "/usr/bin/chromium",
    string WindowsExecutable = @"C:\Program Files\Chromium\Application\chrome.exe",
    string LinuxProfileRoot = "/var/lib/bazaar/browser",
    string WindowsProfileRoot = @"C:\ProgramData\Bazaar\browser")
{
    public const string SectionName = "Bazaar:Browser";
}
