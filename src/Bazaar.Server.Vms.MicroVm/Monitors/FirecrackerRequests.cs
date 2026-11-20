using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>Pure builder of Firecracker REST requests (paths and exact JSON bodies). No I/O; serialisation is source generated.</summary>
public static class FirecrackerRequests
{
    /// <summary>Kernel command line used for every guest.</summary>
    public const string BootArgs = "console=ttyS0 reboot=k panic=1 pci=off";

    public static MicroVmApiRequest MachineConfig(int vcpu, int memoryMb) =>
        Put("/machine-config", JsonSerializer.Serialize(new MachineConfigBody(vcpu, memoryMb), FirecrackerJsonContext.Default.MachineConfigBody));

    public static MicroVmApiRequest BootSource(string kernelPath) =>
        Put("/boot-source", JsonSerializer.Serialize(new BootSourceBody(kernelPath, BootArgs), FirecrackerJsonContext.Default.BootSourceBody));

    public static MicroVmApiRequest RootDrive(string diskPath) =>
        Put("/drives/rootfs", JsonSerializer.Serialize(new DriveBody("rootfs", diskPath, true, false), FirecrackerJsonContext.Default.DriveBody));

    public static MicroVmApiRequest NetworkInterface(string tapName) =>
        Put("/network-interfaces/eth0", JsonSerializer.Serialize(new NetworkBody("eth0", tapName), FirecrackerJsonContext.Default.NetworkBody));

    public static MicroVmApiRequest Vsock(uint guestCid, string udsPath) =>
        Put("/vsock", JsonSerializer.Serialize(new VsockBody(guestCid, udsPath), FirecrackerJsonContext.Default.VsockBody));

    public static MicroVmApiRequest InstanceStart() => Action("InstanceStart");

    public static MicroVmApiRequest SendCtrlAltDel() => Action("SendCtrlAltDel");

    private static MicroVmApiRequest Action(string type) =>
        Put("/actions", JsonSerializer.Serialize(new ActionBody(type), FirecrackerJsonContext.Default.ActionBody));

    private static MicroVmApiRequest Put(string path, string body) => new(HttpMethod.Put, path, body);

    internal sealed record MachineConfigBody(
        [property: JsonPropertyName("vcpu_count")] int VcpuCount,
        [property: JsonPropertyName("mem_size_mib")] int MemSizeMib);

    internal sealed record BootSourceBody(
        [property: JsonPropertyName("kernel_image_path")] string KernelImagePath,
        [property: JsonPropertyName("boot_args")] string BootArgs);

    internal sealed record DriveBody(
        [property: JsonPropertyName("drive_id")] string DriveId,
        [property: JsonPropertyName("path_on_host")] string PathOnHost,
        [property: JsonPropertyName("is_root_device")] bool IsRootDevice,
        [property: JsonPropertyName("is_read_only")] bool IsReadOnly);

    internal sealed record NetworkBody(
        [property: JsonPropertyName("iface_id")] string IfaceId,
        [property: JsonPropertyName("host_dev_name")] string HostDevName);

    internal sealed record VsockBody(
        [property: JsonPropertyName("guest_cid")] uint GuestCid,
        [property: JsonPropertyName("uds_path")] string UdsPath);

    internal sealed record ActionBody([property: JsonPropertyName("action_type")] string ActionType);
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FirecrackerRequests.MachineConfigBody))]
[JsonSerializable(typeof(FirecrackerRequests.BootSourceBody))]
[JsonSerializable(typeof(FirecrackerRequests.DriveBody))]
[JsonSerializable(typeof(FirecrackerRequests.NetworkBody))]
[JsonSerializable(typeof(FirecrackerRequests.VsockBody))]
[JsonSerializable(typeof(FirecrackerRequests.ActionBody))]
internal sealed partial class FirecrackerJsonContext : JsonSerializerContext;
