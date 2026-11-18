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
            [switch]$Force,
            [switch]$DeleteDisk
        )
        $ErrorActionPreference = 'Stop'

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
