using System.Globalization;
using System.Text;
using System.Xml;
using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Kvm;

/// <summary>
/// Builds the libvirt domain XML for a new headless, static-memory VM. Every value goes through <see cref="XmlWriter"/>, which
/// escapes it, so a disk path cannot add elements or attributes. The document has no network interface, no graphics and no sound
/// (egress is default deny), no serial console and no USB; the only host-facing channel is the virtio vsock device.
/// </summary>
public static class KvmDomainXml
{
    private const string _mib = "MiB";

    private const int _minMemoryMb = 512;

    /// <summary>Returns the domain XML document for <paramref name="domain"/>. Uses XmlWriter, never string concatenation.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="domain"/> is null.</exception>
    /// <exception cref="ArgumentException">The name, CPU count, memory or disk path is outside the accepted shape.</exception>
    /// <exception cref="NotSupportedException">The guest OS has no KVM domain layout (Windows VMs run on Hyper-V).</exception>
    public static string Build(KvmDomainDefinition domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        Validate(domain);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        using (var xml = XmlWriter.Create(buffer, settings))
        {
            xml.WriteStartElement("domain");
            xml.WriteAttributeString("type", "kvm");

            WriteElement(xml, "name", domain.Name);
            WriteMemory(xml, "memory", domain.MemoryMb);
            WriteMemory(xml, "currentMemory", domain.MemoryMb);

            // No KSM page sharing between guests: merged pages are a cross-VM timing side channel between agent VMs.
            xml.WriteStartElement("memoryBacking");
            WriteEmpty(xml, "nosharepages");
            xml.WriteEndElement();

            xml.WriteStartElement("vcpu");
            xml.WriteAttributeString("placement", "static");
            xml.WriteString(Int(domain.Vcpu));
            xml.WriteEndElement();

            xml.WriteStartElement("os");
            xml.WriteStartElement("type");
            xml.WriteAttributeString("arch", "x86_64");
            xml.WriteAttributeString("machine", "q35");
            xml.WriteString("hvm");
            xml.WriteEndElement();
            WriteEmpty(xml, "boot", ("dev", "hd"));
            xml.WriteEndElement();

            xml.WriteStartElement("features");
            WriteEmpty(xml, "acpi");
            WriteEmpty(xml, "apic");
            xml.WriteEndElement();

            WriteEmpty(xml, "cpu", ("mode", "host-passthrough"));

            // The guest cannot suspend itself. A suspended domain is neither running nor off, so stop and save would not
            // apply to it, and a wake timer set inside the guest could bring it back later.
            xml.WriteStartElement("pm");
            WriteEmpty(xml, "suspend-to-mem", ("enabled", "no"));
            WriteEmpty(xml, "suspend-to-disk", ("enabled", "no"));
            xml.WriteEndElement();

            WriteElement(xml, "on_poweroff", "destroy");
            WriteElement(xml, "on_reboot", "restart");
            WriteElement(xml, "on_crash", "destroy");

            xml.WriteStartElement("devices");

            xml.WriteStartElement("disk");
            xml.WriteAttributeString("type", "file");
            xml.WriteAttributeString("device", "disk");
            WriteEmpty(xml, "driver", ("name", "qemu"), ("type", "qcow2"), ("cache", "none"));
            WriteEmpty(xml, "source", ("file", domain.DiskPath));
            WriteEmpty(xml, "target", ("dev", "vda"), ("bus", "virtio"));
            xml.WriteEndElement();

            xml.WriteStartElement("vsock");
            xml.WriteAttributeString("model", "virtio");
            WriteEmpty(xml, "cid", ("auto", "yes"));
            xml.WriteEndElement();

            // No USB controller at all: the guest gets no USB bus to enumerate or to attach a device to.
            WriteEmpty(xml, "controller", ("type", "usb"), ("model", "none"));

            // Entropy from the host so the guest does not boot with a starved pool. This is the only device that carries data host-to-guest.
            xml.WriteStartElement("rng");
            xml.WriteAttributeString("model", "virtio");
            xml.WriteStartElement("backend");
            xml.WriteAttributeString("model", "random");
            xml.WriteString("/dev/urandom");
            xml.WriteEndElement();
            xml.WriteEndElement();

            // No serial or console device on purpose: a guest-written host pty is a terminal-escape injection surface for whoever attaches to it.
            WriteEmpty(xml, "memballoon", ("model", "none"));

            xml.WriteEndElement(); // devices
            xml.WriteEndElement(); // domain
        }

        return buffer.ToString();
    }

    private static void Validate(KvmDomainDefinition domain)
    {
        if (domain.Os == VmOs.Windows)
        {
            throw new NotSupportedException("Windows guests are not supported on the KVM backend; they run on Hyper-V.");
        }

        if (domain.Os != VmOs.Linux)
        {
            throw new ArgumentException($"Unknown guest OS '{domain.Os}'.", nameof(domain));
        }

        if (!KvmNames.IsDomainName(domain.Name))
        {
            throw new ArgumentException("Domain name must be a backend-generated name (bazaar-<32 hex>).", nameof(domain));
        }

        if (domain.Vcpu < 1)
        {
            throw new ArgumentException("A domain needs at least one vCPU.", nameof(domain));
        }

        if (domain.MemoryMb < _minMemoryMb)
        {
            throw new ArgumentException($"A domain needs at least {_minMemoryMb} MiB of memory.", nameof(domain));
        }

        if (string.IsNullOrWhiteSpace(domain.DiskPath))
        {
            throw new ArgumentException("A disk path is required.", nameof(domain));
        }

        if (domain.DiskPath.Any(char.IsControl))
        {
            throw new ArgumentException("A disk path must not contain control characters.", nameof(domain));
        }
    }

    private static void WriteElement(XmlWriter xml, string name, string value)
    {
        xml.WriteStartElement(name);
        xml.WriteString(value);
        xml.WriteEndElement();
    }

    private static void WriteMemory(XmlWriter xml, string name, int memoryMb)
    {
        xml.WriteStartElement(name);
        xml.WriteAttributeString("unit", _mib);
        xml.WriteString(Int(memoryMb));
        xml.WriteEndElement();
    }

    private static void WriteEmpty(XmlWriter xml, string name, params (string Attribute, string Value)[] attributes)
    {
        xml.WriteStartElement(name);
        foreach (var (attribute, value) in attributes)
        {
            xml.WriteAttributeString(attribute, value);
        }

        xml.WriteEndElement();
    }

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
}
