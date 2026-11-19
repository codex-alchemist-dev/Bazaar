using System.Globalization;
using System.Xml.Linq;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Kvm;

namespace Bazaar.Server.Vms.Kvm.Tests;

public sealed class KvmDomainXmlTests
{
    private static readonly Guid _domainId = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    private const string _diskPath = "/var/lib/bazaar/disks/child.qcow2";

    private static readonly string[] _headedDevices = ["graphics", "video", "sound", "usb", "input", "serial", "console"];

    private static readonly string[] _allowedDevices = ["controller", "controller", "controller", "controller", "disk", "vsock", "controller", "rng", "watchdog", "memballoon"];

    [Fact]
    public void Pci_ports_are_declared_for_the_three_virtio_devices_and_the_watchdog_cannot_reset()
    {
        var root = System.Xml.Linq.XDocument.Parse(KvmDomainXml.Build(Definition())).Root!;

        var ports = root.Descendants("controller").Where(c => (string?)c.Attribute("model") == "pcie-root-port").ToArray();
        Assert.Equal(3, ports.Length);
        Assert.Equal("none", (string?)root.Descendants("watchdog").Single().Attribute("action"));
    }

    private static KvmDomainDefinition Definition(string? diskPath = null, int vcpu = 2, int memoryMb = 2048) =>
        new(KvmNames.Domain(_domainId), VmOs.Linux, vcpu, memoryMb, diskPath ?? _diskPath);

    private static XElement Parse(string xml) => XDocument.Parse(xml).Root!;

    [Fact]
    public void Build_WritesRequiredSections()
    {
        var domain = Parse(KvmDomainXml.Build(Definition()));

        Assert.Equal("domain", domain.Name.LocalName);
        Assert.Equal("kvm", (string?)domain.Attribute("type"));
        Assert.Equal(KvmNames.Domain(_domainId), (string?)domain.Element("name"));

        var memory = domain.Element("memory")!;
        Assert.Equal("MiB", (string?)memory.Attribute("unit"));
        Assert.Equal("2048", memory.Value);

        var current = domain.Element("currentMemory")!;
        Assert.Equal("MiB", (string?)current.Attribute("unit"));
        Assert.Equal("2048", current.Value);

        var vcpu = domain.Element("vcpu")!;
        Assert.Equal("static", (string?)vcpu.Attribute("placement"));
        Assert.Equal("2", vcpu.Value);

        var os = domain.Element("os")!;
        var type = os.Element("type")!;
        Assert.Equal("x86_64", (string?)type.Attribute("arch"));
        Assert.Equal("q35", (string?)type.Attribute("machine"));
        Assert.Equal("hvm", type.Value);
        Assert.Equal("hd", (string?)os.Element("boot")!.Attribute("dev"));
        Assert.Null(os.Element("loader"));

        var features = domain.Element("features")!;
        Assert.NotNull(features.Element("acpi"));
        Assert.NotNull(features.Element("apic"));

        Assert.Equal("host-passthrough", (string?)domain.Element("cpu")!.Attribute("mode"));

        Assert.Equal("destroy", domain.Element("on_poweroff")!.Value);
        Assert.Equal("restart", domain.Element("on_reboot")!.Value);
        Assert.Equal("destroy", domain.Element("on_crash")!.Value);

        // The guest must not be able to suspend itself out of reach of stop and save.
        Assert.Equal("no", (string?)domain.Element("pm")!.Element("suspend-to-mem")!.Attribute("enabled"));
        Assert.Equal("no", (string?)domain.Element("pm")!.Element("suspend-to-disk")!.Attribute("enabled"));

        var balloon = domain.Element("devices")!.Element("memballoon")!;
        Assert.Equal("none", (string?)balloon.Attribute("model"));
    }

    [Fact]
    public void Build_HasOneVirtioDiskBackedByTheChildPath()
    {
        var devices = Parse(KvmDomainXml.Build(Definition())).Element("devices")!;

        var disks = devices.Elements("disk").ToList();
        Assert.Single(disks);

        var disk = disks[0];
        Assert.Equal("file", (string?)disk.Attribute("type"));
        Assert.Equal("disk", (string?)disk.Attribute("device"));

        var driver = disk.Element("driver")!;
        Assert.Equal("qemu", (string?)driver.Attribute("name"));
        Assert.Equal("qcow2", (string?)driver.Attribute("type"));
        Assert.Equal("none", (string?)driver.Attribute("cache"));

        Assert.Equal(_diskPath, (string?)disk.Element("source")!.Attribute("file"));

        var target = disk.Element("target")!;
        Assert.Equal("vda", (string?)target.Attribute("dev"));
        Assert.Equal("virtio", (string?)target.Attribute("bus"));
    }

    [Fact]
    public void Build_HasVsockAndNoNetworkOrHeadedDevices()
    {
        var devices = Parse(KvmDomainXml.Build(Definition())).Element("devices")!;

        var vsock = devices.Element("vsock")!;
        Assert.Equal("virtio", (string?)vsock.Attribute("model"));
        Assert.Equal("yes", (string?)vsock.Element("cid")!.Attribute("auto"));

        Assert.Empty(devices.Descendants("interface"));
        foreach (var headed in _headedDevices)
        {
            Assert.Empty(devices.Elements(headed));
        }
    }

    [Fact]
    public void Build_DevicesAreExactlyTheAllowedSet()
    {
        var devices = Parse(KvmDomainXml.Build(Definition())).Element("devices")!;

        Assert.Equal(
            _allowedDevices,
            devices.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void Build_EscapesSpecialCharactersInDiskPathWithoutInjectingElements()
    {
        const string hostile = "/srv/a&b<c>'d\"e/<interface type='network'/>&amp;.qcow2";

        var xml = KvmDomainXml.Build(Definition(diskPath: hostile));
        var root = Parse(xml);

        Assert.Equal(hostile, (string?)root.Element("devices")!.Element("disk")!.Element("source")!.Attribute("file"));
        Assert.Empty(root.Descendants("interface"));
        Assert.Single(root.Descendants("disk"));
        Assert.Single(root.Descendants("source"));
        Assert.Equal(KvmNames.Domain(_domainId), (string?)root.Element("name"));
    }

    [Fact]
    public void Build_IsDeterministic()
    {
        var first = KvmDomainXml.Build(Definition());
        var second = KvmDomainXml.Build(Definition());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Build_RejectsNullDomain()
    {
        Assert.Throws<ArgumentNullException>(() => KvmDomainXml.Build(null!));
    }

    [Fact]
    public void Build_RejectsNameOutsideTheBackendShape()
    {
        var domain = Definition() with { Name = "not-a-bazaar-name" };

        Assert.Throws<ArgumentException>(() => KvmDomainXml.Build(domain));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Build_RejectsVcpuBelowOne(int vcpu)
    {
        Assert.Throws<ArgumentException>(() => KvmDomainXml.Build(Definition(vcpu: vcpu)));
    }

    [Theory]
    [InlineData(511)]
    [InlineData(0)]
    public void Build_RejectsMemoryBelow512Mb(int memoryMb)
    {
        Assert.Throws<ArgumentException>(() => KvmDomainXml.Build(Definition(memoryMb: memoryMb)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_RejectsEmptyDiskPath(string? diskPath)
    {
        var domain = Definition() with { DiskPath = diskPath! };

        Assert.Throws<ArgumentException>(() => KvmDomainXml.Build(domain));
    }

    [Theory]
    [InlineData("/var/lib/a\nb.qcow2")]
    [InlineData("/var/lib/a\u0000b.qcow2")]
    [InlineData("/var/lib/a\tb.qcow2")]
    public void Build_RejectsControlCharactersInDiskPath(string diskPath)
    {
        Assert.Throws<ArgumentException>(() => KvmDomainXml.Build(Definition(diskPath: diskPath)));
    }

    [Fact]
    public void Build_RejectsWindowsGuests()
    {
        var domain = Definition() with { Os = VmOs.Windows };

        Assert.Throws<NotSupportedException>(() => KvmDomainXml.Build(domain));
    }

    [Fact]
    public void Build_FormatsNumbersInvariantly()
    {
        var xml = KvmDomainXml.Build(Definition(vcpu: 4, memoryMb: 8192));
        var root = Parse(xml);

        Assert.Equal(8192.ToString(CultureInfo.InvariantCulture), root.Element("memory")!.Value);
        Assert.Equal("4", root.Element("vcpu")!.Value);
    }
}
