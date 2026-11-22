using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>KVM display settings, bound from the <c>Bazaar:VmDisplay:Kvm</c> section.</summary>
public sealed class KvmOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:VmDisplay:Kvm";

    /// <summary>Whether this adapter serves any target.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The libvirt connection URI handed to <c>virsh -c</c>.</summary>
    public string LibvirtUri { get; set; } = "qemu:///system";

    /// <summary>The virsh executable.</summary>
    public string VirshPath { get; set; } = "virsh";

    /// <summary>The directory QEMU writes screendump files into. It must be writable by QEMU and readable by the server.</summary>
    public string ScreendumpDirectory { get; set; } = "/run/bazaar/display";

    /// <summary>Timeout for one virsh call or one socket exchange, in seconds.</summary>
    public int CommandTimeoutSeconds { get; set; } = 10;

    /// <summary>Largest screendump file read, in bytes.</summary>
    public int MaxScreendumpBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> or <see cref="ArgumentException"/> when a value is not usable.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(LibvirtUri))
            throw new ArgumentException("The value must not be empty.", nameof(LibvirtUri));
        if (string.IsNullOrWhiteSpace(VirshPath))
            throw new ArgumentException("The value must not be empty.", nameof(VirshPath));
        if (string.IsNullOrWhiteSpace(ScreendumpDirectory))
            throw new ArgumentException("The value must not be empty.", nameof(ScreendumpDirectory));
        if (CommandTimeoutSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeoutSeconds), CommandTimeoutSeconds, "The value must be at least 1.");
        if (MaxScreendumpBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxScreendumpBytes), MaxScreendumpBytes, "The value must be at least 1.");
    }

    /// <summary>Reads the options from <paramref name="configuration"/> and validates them.</summary>
    public static KvmOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(SectionName);
        var o = new KvmOptions();
        if (bool.TryParse(s[nameof(Enabled)], out bool enabled))
            o.Enabled = enabled;
        o.LibvirtUri = s[nameof(LibvirtUri)] ?? o.LibvirtUri;
        o.VirshPath = s[nameof(VirshPath)] ?? o.VirshPath;
        o.ScreendumpDirectory = s[nameof(ScreendumpDirectory)] ?? o.ScreendumpDirectory;
        o.CommandTimeoutSeconds = ReadInt(s, nameof(CommandTimeoutSeconds), o.CommandTimeoutSeconds);
        o.MaxScreendumpBytes = ReadInt(s, nameof(MaxScreendumpBytes), o.MaxScreendumpBytes);
        o.Validate();
        return o;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
}
