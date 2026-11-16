using System.Xml.Linq;
using Bazaar.Vms.Environments;
using Bazaar.Vms.Lifecycle;

namespace Bazaar.Vms.WindowsSandbox;

/// <summary>Builds the <c>.wsb</c> XML for one sandbox. Pure: no file or host access.</summary>
public static class WindowsSandboxConfig
{
    /// <summary>
    /// Every switch that could leak data or capability is set off: GPU, clipboard, audio, video input and printers.
    /// Mapped folders and a logon command are empty here; scoped mounts come with the mount work package (doc 16 WP8).
    /// Element names follow the documented <c>.wsb</c> schema and are verified on the owner's PC (doc 50 section 8).
    /// </summary>
    public static string Build(VmSpec spec, WindowsSandboxNetworking networking)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Os != VmOs.Windows)
        {
            throw new ArgumentException("Windows Sandbox runs Windows guests only.", nameof(spec));
        }

        if (spec.MemoryMb <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), spec.MemoryMb, "Memory must be positive.");
        }

        var doc = new XDocument(
            new XElement(
                "Configuration",
                new XElement("VGpu", "Disable"),
                new XElement("Networking", networking.ToString()),
                new XElement("MemoryInMB", spec.MemoryMb.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XElement("ClipboardRedirection", "Disable"),
                new XElement("AudioInput", "Disable"),
                new XElement("VideoInput", "Disable"),
                new XElement("PrinterRedirection", "Disable"),
                new XElement("MappedFolders")));
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
