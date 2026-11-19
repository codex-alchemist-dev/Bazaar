using System.Globalization;
using System.Text.RegularExpressions;
using Bazaar.Server.BrowserPool.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.BrowserPool.LinuxHost;

/// <summary>Settings for <see cref="LinuxPoolVmHost"/>. The defaults suit a pool VM whose Bazaar process runs as root.</summary>
public sealed partial class LinuxPoolHostOptions
{
    /// <summary>The configuration section name, under the browser pool's own <c>Bazaar:BrowserPool</c> section.</summary>
    public const string SectionName = "Bazaar:BrowserPool:LinuxHost";

    /// <summary>The nftables <c>inet</c> table the rendered ruleset declares. Must match the renderer's table name.</summary>
    public string TableName { get; set; } = "bazaar_pool";

    /// <summary>
    /// The name prefix of the slot veth interfaces on the VM side when the pool uses one network namespace per slot (its default).
    /// The health check then also requires the drop of everything else arriving from those interfaces, in both the input and the
    /// forward chain. Set it to <c>null</c> only for a pool that runs in the shared-namespace mode, where no such rule is rendered.
    /// </summary>
    public string? SlotInterfacePrefix { get; set; } = "bzv";

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

    /// <summary>
    /// Absolute path of the <c>bazaar-pool-spawn</c> tool (<c>Bazaar.Guest.PoolSpawn</c>). When set, every launch gets a DevTools pipe
    /// (see <see cref="IPoolDevToolsPipes"/>): the tool is put at the front of the launch argv and passes two FIFOs to the browser as
    /// descriptors 3 and 4. Null (the default) means no pipe, and the launch argv is run as given.
    /// </summary>
    public string? PoolSpawnHelperPath { get; set; }

    /// <summary>
    /// Where the FIFOs for the DevTools pipes are made. It must be a root-only directory that no slot can see: it is created with mode
    /// 0700, and a launch whose argv names this directory, or any directory above or below it (a bind into the sandbox), is refused.
    /// Used only when <see cref="PoolSpawnHelperPath"/> is set.
    /// </summary>
    public string PipeDirectory { get; set; } = "/run/bazaar-pool/pipes";

    /// <summary>Returns the reasons these options are unusable; empty when they are fine.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!TableNamePattern().IsMatch(TableName))
        {
            errors.Add("TableName must be an nftables identifier (letters, digits, underscore; at most 64).");
        }

        if (SlotInterfacePrefix is not null && !InterfacePrefixPattern().IsMatch(SlotInterfacePrefix))
        {
            errors.Add("SlotInterfacePrefix must be 1 to 8 letters or digits, or null.");
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

        if (PoolSpawnHelperPath is not null)
        {
            if (!PoolPipePlan.IsSafeAbsolutePath(PoolSpawnHelperPath))
            {
                errors.Add("PoolSpawnHelperPath must be an absolute path (letters, digits and . _ + - only, no '..'), or null.");
            }

            if (!PoolPipePlan.IsSafeAbsolutePath(PipeDirectory))
            {
                errors.Add("PipeDirectory must be an absolute path (letters, digits and . _ + - only, no '..').");
            }
            else if (PoolSpawnHelperPath is not null && PoolPipePlan.IsSafeAbsolutePath(PoolSpawnHelperPath) && PoolPipePlan.Overlaps(PipeDirectory, PoolSpawnHelperPath))
            {
                errors.Add("PoolSpawnHelperPath may not sit inside PipeDirectory.");
            }
        }

        return errors;
    }

    /// <summary>
    /// Reads the options from <paramref name="configuration"/> (the <see cref="SectionName"/> section) and validates them. Time spans
    /// are written <c>hh:mm:ss</c>. An empty <see cref="SlotInterfacePrefix"/> or <see cref="PoolSpawnHelperPath"/> means null.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value cannot be read or is out of range. The message names the key, never the value.</exception>
    public static LinuxPoolHostOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(SectionName);
        var o = new LinuxPoolHostOptions();
        o.TableName = s[nameof(TableName)] ?? o.TableName;
        if (s[nameof(SlotInterfacePrefix)] is { } prefix)
        {
            o.SlotInterfacePrefix = prefix.Length == 0 ? null : prefix;
        }

        o.NftPath = s[nameof(NftPath)] ?? o.NftPath;
        o.CommandTimeout = ReadSpan(s, nameof(CommandTimeout), o.CommandTimeout);
        o.FirewallTimeout = ReadSpan(s, nameof(FirewallTimeout), o.FirewallTimeout);
        o.StopTimeout = ReadSpan(s, nameof(StopTimeout), o.StopTimeout);
        o.MaxCapturedChars = ReadInt(s, nameof(MaxCapturedChars), o.MaxCapturedChars);
        if (s[nameof(PoolSpawnHelperPath)] is { } helper)
        {
            o.PoolSpawnHelperPath = helper.Length == 0 ? null : helper;
        }

        o.PipeDirectory = s[nameof(PipeDirectory)] ?? o.PipeDirectory;
        IReadOnlyList<string> errors = o.Validate();
        if (errors.Count != 0)
        {
            throw new InvalidOperationException($"Invalid {SectionName} configuration: " + string.Join(' ', errors));
        }

        return o;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        section[key] is not { } text ? fallback
        : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v
        : throw new InvalidOperationException($"{SectionName}:{key} is not a whole number.");

    private static TimeSpan ReadSpan(IConfigurationSection section, string key, TimeSpan fallback) =>
        section[key] is not { } text ? fallback
        : TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan v) ? v
        : throw new InvalidOperationException($"{SectionName}:{key} is not a time span (hh:mm:ss).");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex TableNamePattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,8}$")]
    private static partial Regex InterfacePrefixPattern();
}
