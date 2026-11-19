using System.Text.RegularExpressions;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>Settings for <see cref="LinuxPoolVmHost"/>. The defaults suit a pool VM whose Bazaar process runs as root.</summary>
public sealed partial class LinuxPoolHostOptions
{
    /// <summary>The nftables <c>inet</c> table the rendered ruleset declares. Must match the renderer's table name.</summary>
    public string TableName { get; set; } = "bazaar_pool";

    /// <summary>The <c>nft</c> program (an absolute path in production, so the search path cannot redirect it).</summary>
    public string NftPath { get; set; } = "nft";

    /// <summary>How long one prepare or cleanup command may run.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long one <c>nft</c> call (apply, list) may run.</summary>
    public TimeSpan FirewallTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long <see cref="LinuxPoolVmHost.StopAsync"/> waits for a killed browser to be reaped.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most stdout and the most stderr characters kept per command. The rest is discarded.</summary>
    public int MaxCapturedChars { get; set; } = 16 * 1024;

    /// <summary>Returns the reasons these options are unusable; empty when they are fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!TableNamePattern().IsMatch(TableName))
        {
            errors.Add("TableName must be an nftables identifier (letters, digits, underscore; at most 64).");
        }

        if (string.IsNullOrWhiteSpace(NftPath))
        {
            errors.Add("NftPath is required.");
        }

        if (CommandTimeout <= TimeSpan.Zero || FirewallTimeout <= TimeSpan.Zero || StopTimeout <= TimeSpan.Zero)
        {
            errors.Add("Timeouts must be positive.");
        }

        if (MaxCapturedChars is < 256 or > 1024 * 1024)
        {
            errors.Add("MaxCapturedChars must be 256 to 1048576.");
        }

        return errors;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex TableNamePattern();
}
