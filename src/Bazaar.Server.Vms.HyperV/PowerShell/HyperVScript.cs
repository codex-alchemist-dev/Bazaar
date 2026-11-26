using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Vms.HyperV.PowerShell;

/// <summary>
/// The one PowerShell script the provider runs (doc 16: typed parameters, never string-built). It is fixed text: an operation is a
/// <c>ValidateSet</c> value and every other input is a bound parameter, so no value is ever parsed as code. The script validates
/// names again with the same patterns as <see cref="HyperVNames"/>. It writes only the VM state to standard output.
/// </summary>
public static class HyperVScript
{
    /// <summary>The operations the script accepts, in the order of its <c>ValidateSet</c>.</summary>
    public static IReadOnlyList<string> Operations { get; } =
        ["CreateDisk", "CreateVm", "Start", "Stop", "Save", "Checkpoint", "Restore", "Remove", "State"];

    public const string Text = """
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][ValidateSet('CreateDisk', 'CreateVm', 'Start', 'Stop', 'Save', 'Checkpoint', 'Restore', 'Remove', 'State')][string]$Operation,
            [Parameter(Mandatory)][ValidatePattern('^bazaar-[0-9a-f]{32}$')][string]$Name,
            [string]$ParentPath,
            [string]$DiskPath,
            [ValidateSet('Windows', 'Linux')][string]$GuestOs,
            [ValidateRange(1, 64)][int]$Vcpu = 2,
            [ValidateRange(512, 1048576)][int]$MemoryMb = 2048,
            [ValidatePattern('^bazaar-cp-[0-9a-f]{32}$')][string]$CheckpointName,
            [Parameter(Mandatory)][ValidatePattern('(?-i)^bazaar-[0-9a-f]{32}\z')][string]$Name,
            [ValidatePattern('(?-i)^[A-Za-z]:\\(?!(.*\\)?\.\.(\\|\z))[^*?\[\]<>|"\x00-\x1f]+\.vhdx\z')][string]$ParentPath,
            [ValidatePattern('(?-i)^[A-Za-z]:\\(?!(.*\\)?\.\.(\\|\z))[^*?\[\]<>|"\x00-\x1f]+\.vhdx\z')][string]$DiskPath,
            [ValidateSet('Windows', 'Linux')][string]$GuestOs,
            [ValidateRange(1, 64)][int]$Vcpu = 2,
            [ValidateRange(512, 1048576)][int]$MemoryMb = 2048,
            [ValidatePattern('(?-i)^bazaar-cp-[0-9a-f]{32}\z')][string]$CheckpointName,
            [switch]$Force,
            [switch]$DeleteDisk
        )
        $ErrorActionPreference = 'Stop'

        # A disk may be deleted only when it sits in a directory named for this VM (the layout the provider creates). A VM whose disk was
        # re-pointed elsewhere keeps that other file. With -Standard the file must also be the provider's own 'disk.vhdx'.
        function Test-OwnedDisk([string]$Path, [string]$VmName, [switch]$Standard) {
            if ([string]::IsNullOrEmpty($Path) -or $Path -match '[*?\[\]]') { return $false }
            $full = [System.IO.Path]::GetFullPath($Path)
            if ([System.IO.Path]::GetExtension($full) -ne '.vhdx') { return $false }
            if ($Standard -and [System.IO.Path]::GetFileName($full) -ne 'disk.vhdx') { return $false }
            $directory = [System.IO.Path]::GetDirectoryName($full)
            if ([string]::IsNullOrEmpty($directory)) { return $false }
            return ([System.IO.Path]::GetFileName($directory) -eq $VmName)
        }

        switch ($Operation) {
            'CreateDisk' {
                New-VHD -Path $DiskPath -ParentPath $ParentPath -Differencing | Out-Null
            }
            'CreateVm' {
                $vm = New-VM -Name $Name -Generation 2 -MemoryStartupBytes ([int64]$MemoryMb * 1MB) -VHDPath $DiskPath
                Set-VM -VM $vm -ProcessorCount $Vcpu -CheckpointType Standard -AutomaticCheckpointsEnabled $false -AutomaticStartAction Nothing -AutomaticStopAction Save
                Set-VMMemory -VM $vm -DynamicMemoryEnabled $false
                if ($GuestOs -eq 'Linux') {
                    Set-VMFirmware -VM $vm -SecureBootTemplate 'MicrosoftUEFICertificateAuthority'
                }
                else {
                    Set-VMFirmware -VM $vm -SecureBootTemplate 'MicrosoftWindows'
                }
                # The VM is created with no switch, so its network adapter is disconnected: default-deny egress (doc 16).
                # Everything below removes attack surface explicitly instead of relying on defaults (doc 16: the guest is hostile).
                # Guest Service Interface lets the host copy files into the guest, and Key-Value Pair Exchange and VSS add host-guest data channels.
                # Matched by the language-neutral component GUID at the end of the Id, never by the localized name. Heartbeat, Shutdown and
                # Time Synchronization stay, because the backend needs them.
                $unneeded = @('6C09BB55-D683-4DA0-8931-C9BF705F6480', '2A34B1C2-FD73-4043-8A5B-DD2159BC743F', '5CED1297-4598-4915-A5FC-AD21BB4D02A4')
                Get-VMIntegrationService -VM $vm | Where-Object { $unneeded -contains ([string]$_.Id).Split('\')[-1] } | Disable-VMIntegrationService
                # No optical drive: nothing for the guest to read or for an ISO to be mounted into.
                Get-VMDvdDrive -VM $vm | Remove-VMDvdDrive
                # Every network adapter is disconnected, whatever New-VM created.
                Get-VMNetworkAdapter -VM $vm | Disconnect-VMNetworkAdapter
                # No nested virtualization: the guest must not get a hypervisor of its own.
                Set-VMProcessor -VM $vm -ExposeVirtualizationExtensions $false
            }
            'Start' { Start-VM -Name $Name | Out-Null }
            'Stop' {
                if ($Force) { Stop-VM -Name $Name -TurnOff | Out-Null } else { Stop-VM -Name $Name | Out-Null }
            }
            'Save' { Save-VM -Name $Name | Out-Null }
            'Checkpoint' { Checkpoint-VM -Name $Name -SnapshotName $CheckpointName | Out-Null }
            'Restore' { Restore-VMSnapshot -VMName $Name -Name $CheckpointName -Confirm:$false | Out-Null }
            'Remove' {
                $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
                if ($null -ne $vm) {
                    $disks = @($vm.HardDrives | ForEach-Object { $_.Path })
                    Get-VMSnapshot -VMName $Name -ErrorAction SilentlyContinue | Remove-VMSnapshot -IncludeAllChildSnapshots -ErrorAction SilentlyContinue
                    Remove-VM -Name $Name -Force
                    if ($DeleteDisk) {
                        foreach ($path in $disks) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
                    }
                }
                elseif ($DeleteDisk -and $DiskPath -and (Test-Path -LiteralPath $DiskPath)) {
                        foreach ($path in $disks) { if ((Test-OwnedDisk -Path $path -VmName $Name) -and (Test-Path -LiteralPath $path)) { Remove-Item -LiteralPath $path -Force } }
                    }
                }
                elseif ($DeleteDisk -and $DiskPath -and (Test-OwnedDisk -Path $DiskPath -VmName $Name -Standard) -and (Test-Path -LiteralPath $DiskPath)) {
                    Remove-Item -LiteralPath $DiskPath -Force
                }
            }
            'State' {
                $vm = Get-VM -Name $Name -ErrorAction SilentlyContinue
                if ($null -eq $vm) { 'Missing' } else { [string]$vm.State }
            }
        }
        """;

    /// <summary>A short hash of <see cref="Text"/> for the script's file name, so a changed script is a new file.</summary>
    public static string Hash()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Text));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }
}
