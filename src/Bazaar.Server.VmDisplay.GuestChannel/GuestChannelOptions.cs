using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay.GuestChannel;

/// <summary>Settings of the guest channel display adapter. Bound from <c>Bazaar:VmDisplayGuestChannel</c> (docs/design/59, PROPOSED).</summary>
public sealed record GuestChannelDisplayOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Bazaar:VmDisplayGuestChannel";

    /// <summary>
    /// The working directory every exec runs in, an absolute guest path. The guest agent refuses an exec whose directory is outside its
    /// allowed roots, so the Linux guest must be started with this directory as a root. Default <c>/run/bazaar/display</c>.
    /// </summary>
    public string ExecWorkingDirectory { get; init; } = "/run/bazaar/display";

    /// <summary>The longest timeout one exec may ask the guest for, in seconds. A larger request is cut to this. Default 60, 1 to 600.</summary>
    public int MaxCallTimeoutSeconds { get; init; } = 60;

    /// <summary>The longest wait for the channel directory to answer, in seconds. Default 5, 1 to 60.</summary>
    public int LookupTimeoutSeconds { get; init; } = 5;

    /// <summary>Binds the options and rejects values out of range.</summary>
    /// <exception cref="InvalidOperationException">A value is out of range or malformed.</exception>
    public static GuestChannelDisplayOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection section = configuration.GetSection(SectionName);
        var defaults = new GuestChannelDisplayOptions();
        string directory = section[nameof(ExecWorkingDirectory)] ?? defaults.ExecWorkingDirectory;
        ValidateDirectory(directory);
        return new GuestChannelDisplayOptions
        {
            ExecWorkingDirectory = directory,
            MaxCallTimeoutSeconds = ReadInt(section, nameof(MaxCallTimeoutSeconds), defaults.MaxCallTimeoutSeconds, 1, 600),
            LookupTimeoutSeconds = ReadInt(section, nameof(LookupTimeoutSeconds), defaults.LookupTimeoutSeconds, 1, 60),
        };
    }

    private static int ReadInt(IConfigurationSection section, string name, int fallback, int min, int max)
    {
        int value = fallback;
        string? raw = section[name];
        if (raw is not null && !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw new InvalidOperationException($"{SectionName}:{name} must be a whole number.");
        if (value < min || value > max)
            throw new InvalidOperationException($"{SectionName}:{name} must be between {min} and {max}.");
        return value;
    }

    private static void ValidateDirectory(string directory)
    {
        bool ok = directory.Length > 1
            && directory[0] == '/'
            && directory[^1] != '/'
            && !directory.Contains('\0', StringComparison.Ordinal)
            && !directory.Contains('\\', StringComparison.Ordinal)
            && !directory.Split('/').Skip(1).Any(s => s.Length == 0 || s is "." or "..");
        if (!ok)
            throw new InvalidOperationException($"{SectionName}:{nameof(ExecWorkingDirectory)} must be an absolute guest path with no empty, '.' or '..' segment, and not the root.");
    }
}
