using System.Text;
using Bazaar.Server.SealedGit;

namespace Bazaar.Server.SealedVault;

/// <summary>
/// The default <see cref="IMountRootVerifier"/> (doc 61 "Vault adoption"). The owner ruled (2026-11-20, DECIDED) that a mount may hold plaintext
/// only on tmpfs or the doc 63 encrypted volume. On Linux a root on <c>tmpfs</c> (or <c>ramfs</c>) is detected from <c>/proc/self/mounts</c>.
/// An encrypted volume cannot be told apart from a plain disk here, so the operator attests it with <c>Bazaar:SealedVault:MountRootAttested=true</c>.
/// Anything else is not protected, and the mount is refused.
/// </summary>
public sealed class ProcMountsRootVerifier : IMountRootVerifier
{
    private readonly bool _attested;
    private readonly string _mountsFile;

    /// <param name="attested">The operator attests that the mount root is the encrypted volume (or tmpfs).</param>
    /// <param name="mountsFile">The mount table to read; tests pass their own.</param>
    public ProcMountsRootVerifier(bool attested, string mountsFile = "/proc/self/mounts")
    {
        _attested = attested;
        _mountsFile = mountsFile;
    }

    public MountRootVerdict Verify(string mountRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountRoot);
        if (_attested)
        {
            return new MountRootVerdict(true, "operator-attested encrypted volume or tmpfs (Bazaar:SealedVault:MountRootAttested)");
        }

        if (!OperatingSystem.IsLinux() || !File.Exists(_mountsFile))
        {
            return new MountRootVerdict(false, "the volume type cannot be detected on this host; attest it with Bazaar:SealedVault:MountRootAttested");
        }

        string? bestPoint = null;
        string? bestType = null;
        foreach (var line in File.ReadLines(_mountsFile))
        {
            var fields = line.Split(' ');
            if (fields.Length < 3)
            {
                continue;
            }

            var point = Unescape(fields[1]);
            if ((SealedPaths.IsAtOrInside(mountRoot, point) || point == "/") && (bestPoint is null || point.Length >= bestPoint.Length))
            {
                bestPoint = point;
                bestType = fields[2];
            }
        }

        return bestType is "tmpfs" or "ramfs"
            ? new MountRootVerdict(true, $"{bestType} at {bestPoint}")
            : new MountRootVerdict(false, $"{bestType ?? "unknown"} at {bestPoint ?? "?"} is not tmpfs; attest the encrypted volume with Bazaar:SealedVault:MountRootAttested");
    }

    /// <summary>Undoes the octal escapes of the mount table (<c>\040</c> for a space, and so on).</summary>
    private static string Unescape(string field)
    {
        var sb = new StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length && field[(i + 1)..(i + 4)].All(c => c is >= '0' and <= '7'))
            {
                sb.Append((char)Convert.ToInt32(field[(i + 1)..(i + 4)], 8));
                i += 3;
            }
            else
            {
                sb.Append(field[i]);
            }
        }

        return sb.ToString();
    }
}
