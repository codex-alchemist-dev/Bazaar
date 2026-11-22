using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.Image;

/// <summary>Operator settings for the pool VM image (section <c>Bazaar:BrowserPool:Image</c>).</summary>
public sealed partial class PoolImageOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:BrowserPool:Image";

    /// <summary>The shortest allowed recheck interval.</summary>
    public static readonly TimeSpan MinRecheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>The longest allowed recheck interval.</summary>
    public static readonly TimeSpan MaxRecheckInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The packages the provisioning script installs. The default covers what the pool needs on Ubuntu 24.04. Note that Ubuntu's own
    /// <c>chromium</c> package is a snap shim there; an operator who needs a real deb replaces this list (and the Chromium path) to suit
    /// their package source.
    /// </summary>
    public IList<string> Packages { get; set; } =
        ["chromium", "bubblewrap", "util-linux", "procps", "nftables", "iproute2", "cage", "apparmor"];

    /// <summary>
    /// The directory on the VM where the operator copied the published tools (<c>bazaar-pool-spawn</c>, <c>bazaar-pointer</c>). Null
    /// (the default) means the script has no install step. When set, the script needs the matching <see cref="PoolToolBundle"/>.
    /// </summary>
    public string? ToolSourceDirectory { get; set; }

    /// <summary>Where the script installs the tools: root-owned, mode 0755. Needs at least two path segments.</summary>
    public string ToolInstallDirectory { get; set; } = "/opt/bazaar/bin";

    /// <summary>
    /// Where the tool manifest (<c>pool-tools.json</c>) is on the VM, directly inside <see cref="ToolInstallDirectory"/>; the script
    /// writes it there. Null (the default) means the readiness check does not compare tool hashes. When set, it must.
    /// </summary>
    public string? ToolManifestPath { get; set; }

    /// <summary>How long a passing readiness result is reused. A failure is never reused. 10 seconds to 1 hour.</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Returns the reasons these options are unusable; empty when they are fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (Packages is null || Packages.Count == 0 || Packages.Count > 100)
        {
            errors.Add("Packages must list 1 to 100 package names.");
        }
        else if (Packages.Any(p => p is null || !PackagePattern().IsMatch(p)))
        {
            errors.Add("Every Packages entry must be a Debian package name (lower-case letters, digits and + - . only, 2 to 64 characters, starting with a letter or digit).");
        }

        if (RecheckInterval < MinRecheckInterval || RecheckInterval > MaxRecheckInterval)
        {
            errors.Add("RecheckInterval must be 10 seconds to 1 hour.");
        }

        if (ToolSourceDirectory is not null && !BrowserPoolOptionsValidator.IsSafeAbsolutePath(ToolSourceDirectory))
        {
            errors.Add("ToolSourceDirectory must be a safe absolute path.");
        }

        bool installOk = BrowserPoolOptionsValidator.IsSafeAbsolutePath(ToolInstallDirectory)
            && ToolInstallDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;
        if (!installOk)
        {
            errors.Add("ToolInstallDirectory must be a safe absolute path with at least two segments.");
        }

        if (ToolManifestPath is not null)
        {
            if (!BrowserPoolOptionsValidator.IsSafeAbsolutePath(ToolManifestPath))
            {
                errors.Add("ToolManifestPath must be a safe absolute path.");
            }
            else if (installOk && ToolManifestPath != ToolInstallDirectory.TrimEnd('/') + "/" + PoolToolBundle.ManifestFileName)
            {
                errors.Add($"ToolManifestPath must be {PoolToolBundle.ManifestFileName} directly inside ToolInstallDirectory.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Reads the options from <paramref name="configuration"/> and validates them. The time span is written <c>hh:mm:ss</c>. A
    /// <c>Packages</c> list in the configuration replaces the default list.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value cannot be read or is out of range. The message names the key, never the value.</exception>
    public static PoolImageOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(SectionName);
        var o = new PoolImageOptions();
        var packages = s.GetSection(nameof(Packages)).GetChildren().Select(c => c.Value ?? string.Empty).ToList();
        if (packages.Count != 0)
        {
            o.Packages = packages;
        }

        if (s[nameof(RecheckInterval)] is { } text)
        {
            o.RecheckInterval = TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan v)
                ? v
                : throw new InvalidOperationException($"{SectionName}:{nameof(RecheckInterval)} is not a time span (hh:mm:ss).");
        }

        o.ToolSourceDirectory = s[nameof(ToolSourceDirectory)] ?? o.ToolSourceDirectory;
        o.ToolInstallDirectory = s[nameof(ToolInstallDirectory)] ?? o.ToolInstallDirectory;
        o.ToolManifestPath = s[nameof(ToolManifestPath)] ?? o.ToolManifestPath;

        IReadOnlyList<string> errors = o.Validate();
        if (errors.Count != 0)
        {
            throw new InvalidOperationException($"Invalid {SectionName} configuration: " + string.Join(' ', errors));
        }

        return o;
    }

    /// <summary>True when the name is a safe Debian package name.</summary>
    internal static bool IsValidPackageName(string? name) => name is not null && PackagePattern().IsMatch(name);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9+.\-]{1,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackagePattern();
}
