using System.Xml.Linq;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>Builds the <c>.wsb</c> XML for one sandbox. Pure: no file or host access.</summary>
public static class WindowsSandboxConfig
{
    /// <summary>Where the read-only agent folder appears inside the guest.</summary>
    public const string GuestAgentFolder = @"C:\Bazaar\agent";

    /// <summary>
    /// Every switch that could leak data or capability is set explicitly: GPU, clipboard, audio, video input and printers off, protected client on (the sandbox window runs isolated from the host session).
    /// The only mapped folder is the guest agent build (<paramref name="agentFolder"/>), always read-only (doc 50 section 9).
    /// There is no logon command: it does not run on the verified host, so the host starts the agent with <c>wsb exec</c>.
    /// Scoped mounts for the Soul folder and workspace come with the mount work package (doc 16 WP8).
    /// Element names follow the documented <c>.wsb</c> schema and are verified on the owner's PC (doc 50 section 8).
    /// </summary>
    public static string Build(VmSpec spec, WindowsSandboxNetworking networking, string? agentFolder = null)
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

        if (!Enum.IsDefined(networking))
        {
            throw new ArgumentOutOfRangeException(nameof(networking), networking, "Unknown networking mode.");
        }

        var mappedFolders = new XElement("MappedFolders");
        if (!string.IsNullOrWhiteSpace(agentFolder))
        {
            if (!WindowsSandboxConfigPolicy.IsSafeHostFolder(agentFolder))
            {
                throw new ArgumentException("The agent folder must be a local absolute path with a drive letter (no network path, no '..').", nameof(agentFolder));
            }

            mappedFolders.Add(new XElement(
                "MappedFolder",
                new XElement("HostFolder", agentFolder),
                new XElement("SandboxFolder", GuestAgentFolder),
                new XElement("ReadOnly", "true")));
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
                new XElement("ProtectedClient", "Enable"),
                mappedFolders));
        var xml = doc.ToString(SaveOptions.DisableFormatting);
        WindowsSandboxConfigPolicy.Validate(xml);
        return xml;
    }
}
