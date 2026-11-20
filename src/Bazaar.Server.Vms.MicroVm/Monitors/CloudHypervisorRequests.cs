using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>Pure builder of Cloud Hypervisor REST requests (paths and exact JSON bodies). No I/O; serialisation is source generated.</summary>
public static class CloudHypervisorRequests
{
    public const string CommandLine = "console=ttyS0 reboot=k panic=1 root=/dev/vda rw";

    public static MicroVmApiRequest Create(int vcpu, int memoryMb, string kernelPath, string diskPath, string tapName, uint guestCid, string vsockSocketPath)
    {
        var body = new VmConfig(
            new Cpus(vcpu, vcpu),
            new Memory((long)memoryMb * 1024 * 1024),
            new Payload(kernelPath, CommandLine),
            [new Disk(diskPath)],
            [new Net(tapName)],
            new Vsock(guestCid, vsockSocketPath));
        return new(HttpMethod.Put, "/api/v1/vm.create", JsonSerializer.Serialize(body, CloudHypervisorJsonContext.Default.VmConfig));
    }

    public static MicroVmApiRequest Boot() => new(HttpMethod.Put, "/api/v1/vm.boot", null);

    public static MicroVmApiRequest Shutdown() => new(HttpMethod.Put, "/api/v1/vm.shutdown", null);

    public static MicroVmApiRequest PowerButton() => new(HttpMethod.Put, "/api/v1/vm.power-button", null);

    internal sealed record VmConfig(
        [property: JsonPropertyName("cpus")] Cpus Cpus,
        [property: JsonPropertyName("memory")] Memory Memory,
        [property: JsonPropertyName("payload")] Payload Payload,
        [property: JsonPropertyName("disks")] Disk[] Disks,
        [property: JsonPropertyName("net")] Net[] Net,
        [property: JsonPropertyName("vsock")] Vsock Vsock);

    internal sealed record Cpus(
        [property: JsonPropertyName("boot_vcpus")] int BootVcpus,
        [property: JsonPropertyName("max_vcpus")] int MaxVcpus);

    internal sealed record Memory([property: JsonPropertyName("size")] long Size);

    internal sealed record Payload(
        [property: JsonPropertyName("kernel")] string Kernel,
        [property: JsonPropertyName("cmdline")] string Cmdline);

    internal sealed record Disk([property: JsonPropertyName("path")] string Path);

    internal sealed record Net([property: JsonPropertyName("tap")] string Tap);

    internal sealed record Vsock(
        [property: JsonPropertyName("cid")] uint Cid,
        [property: JsonPropertyName("socket")] string Socket);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CloudHypervisorRequests.VmConfig))]
internal sealed partial class CloudHypervisorJsonContext : JsonSerializerContext;
