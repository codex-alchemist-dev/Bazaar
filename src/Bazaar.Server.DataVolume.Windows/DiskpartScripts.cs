namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// Pure builders of the diskpart scripts, written to diskpart's stdin. diskpart ships on every Windows edition, unlike the
/// Hyper-V cmdlets. Nothing secret is ever in a diskpart script: the volume key goes only to the BitLocker seam.
/// </summary>
internal static class DiskpartScripts
{
    /// <summary>The file system label of the data volume.</summary>
    public const string VolumeLabel = "BazaarData";

    /// <summary>The smallest container: BitLocker needs a few tens of MiB for its metadata.</summary>
    public const long MinimumSizeBytes = 64L * 1024 * 1024;

    public static string CreateVdisk(string vhdxPath, long maxSizeBytes)
    {
        ScriptPaths.Validate(vhdxPath, nameof(vhdxPath), requireVhdx: true);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSizeBytes, MinimumSizeBytes);
        var megabytes = (maxSizeBytes + (1024 * 1024) - 1) / (1024 * 1024);
        return Script($"create vdisk file=\"{vhdxPath}\" maximum={megabytes} type=expandable");
    }

    public static string Attach(string vhdxPath)
    {
        ScriptPaths.Validate(vhdxPath, nameof(vhdxPath), requireVhdx: true);
        return Script($"select vdisk file=\"{vhdxPath}\"", "attach vdisk");
    }

    public static string Detach(string vhdxPath)
    {
        ScriptPaths.Validate(vhdxPath, nameof(vhdxPath), requireVhdx: true);
        return Script($"select vdisk file=\"{vhdxPath}\"", "detach vdisk");
    }

    /// <summary>GPT, one NTFS partition, mounted on a folder with no drive letter.</summary>
    public static string InitialiseAndMount(string vhdxPath, string mountFolder)
    {
        ScriptPaths.Validate(vhdxPath, nameof(vhdxPath), requireVhdx: true);
        var folder = ScriptPaths.ValidateFolder(mountFolder, nameof(mountFolder));
        return Script(
            $"select vdisk file=\"{vhdxPath}\"",
            "convert gpt",
            "create partition primary",
            $"format fs=ntfs quick label={VolumeLabel}",
            $"assign mount=\"{folder}\"");
    }

    private static string Script(params string[] lines) => string.Join('\n', lines) + "\nexit\n";
}
