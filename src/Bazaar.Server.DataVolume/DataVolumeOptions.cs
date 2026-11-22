namespace Bazaar.Server.DataVolume;

/// <summary>
/// Host configuration for the data volume (doc 63 section 2), bound from <c>Bazaar:Host</c>. Setup writes the install id,
/// the volume id and the instance class; the host only reads them.
/// </summary>
public sealed class DataVolumeOptions
{
    public const string BootRootKey = "Bazaar:Host:BootRoot";
    public const string DataRootKey = "Bazaar:Host:DataRoot";
    public const string InstallIdKey = "Bazaar:Host:InstallId";
    public const string VolumeIdKey = "Bazaar:Host:VolumeId";
    public const string InstanceClassKey = "Bazaar:Host:InstanceClass";

    /// <summary>The plain folder holding the KEK file, the descriptor and the container. Never inside the data root.</summary>
    public string BootRoot { get; set; } = string.Empty;

    /// <summary>The mount point of the encrypted volume.</summary>
    public string DataRoot { get; set; } = string.Empty;

    public string InstallId { get; set; } = string.Empty;

    public string VolumeId { get; set; } = string.Empty;

    /// <summary>Production unless setup created a development instance.</summary>
    public InstanceClass InstanceClass { get; set; } = InstanceClass.Production;

    /// <summary>True when the process runs with <c>DOTNET_ENVIRONMENT=Development</c>. One of the conditions for <c>dev-plain</c>.</summary>
    public bool IsDevelopmentEnvironment { get; set; }

    /// <summary>The descriptor path under the boot root.</summary>
    public string DescriptorPath => Path.Combine(BootRoot, "volume", "descriptor.json");

    /// <summary>The marker path inside the data root.</summary>
    public string MarkerPath => Path.Combine(DataRoot, VolumeMarker.FileName);
}
