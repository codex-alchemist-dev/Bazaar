namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>
/// Cloud Hypervisor adapter: launches <c>cloud-hypervisor --api-socket</c> and drives vm.create, vm.boot and vm.power-button over
/// its REST API; the process is killed after the stop timeout. It has no jailer: <see cref="MicroVmOptions.JailerPath"/> is ignored.
/// </summary>
public sealed class CloudHypervisorMonitor(
    MicroVmOptions options,
    IMicroVmApiClient api,
    IMicroVmProcessLauncher launcher,
    IMicroVmTapNetwork network,
    IMicroVmDiskCloner? cloner = null,
    TimeSpan? stopTimeout = null,
    TimeSpan? connectDelay = null)
    : MicroVmMonitorBase(options, api, launcher, network, cloner, stopTimeout, connectDelay)
{
    protected override IReadOnlyList<string> BuildLaunchArgv(VmConfig config) =>
        [Options.MonitorBinaryPath!, "--api-socket", config.Paths.ApiSocket];

    protected override IReadOnlyList<MicroVmApiRequest> BuildBootRequests(VmConfig config) =>
    [
        CloudHypervisorRequests.Create(config.Vcpu, config.MemoryMb, config.Paths.ApiKernel, config.Paths.ApiDisk, config.Tap, config.Cid, config.Paths.ApiVsock),
        CloudHypervisorRequests.Boot(),
    ];

    protected override MicroVmApiRequest BuildGracefulStopRequest() => CloudHypervisorRequests.PowerButton();
}
