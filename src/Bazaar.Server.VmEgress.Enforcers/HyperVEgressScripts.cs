namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>
/// The PowerShell the Hyper-V enforcer runs, as compile-time constants. Data (the VM name and the rules) reaches the scripts only
/// as JSON in the <see cref="ParametersVariable"/> environment variable, read as <c>$p</c>; no caller value is ever part of the
/// script text or the command line. The script text goes to PowerShell on standard input.
/// </summary>
/// <remarks>
/// UNVERIFIED: no script here has run against a live Hyper-V host. The cmdlet names and parameters follow the Microsoft
/// documentation for the Hyper-V module (Get, Add and Remove-VMNetworkAdapterExtendedAcl); how the host prints <c>Protocol</c>,
/// <c>RemotePort</c> and <c>RemoteIPAddress</c> for wildcard rules is a guess that the NUC run (Category=Hyperv) must confirm.
/// Extended ACLs carry no tag, so "the agent's ACLs" means every extended ACL on the agent's VM adapters: those adapters belong to
/// the agent's VM and this module owns all rules on them.
/// </remarks>
public static class HyperVEgressScripts
{
    /// <summary>The environment variable that carries the parameter JSON.</summary>
    public const string ParametersVariable = "BAZAAR_EGRESS_PARAMS";

    /// <summary>Marker comments, so a test can assert the order of the steps in <see cref="Apply"/>.</summary>
    internal const string StepDenyAdded = "# STEP 1: deny-all rules are added first";

    internal const string StepStaleRemoved = "# STEP 2: rules that are not wanted are removed";

    internal const string StepAllowsAdded = "# STEP 3: wanted allow rules that are missing are added";

    /// <summary>
    /// Reads <c>$p.VmName</c> and <c>$p.Rules</c> (JSON array of <c>{Action, Direction, RemoteIPAddress, Protocol, RemotePort, Weight,
    /// Stateful}</c>) and makes the VM's extended ACLs exactly that set. Ordering is fail closed: deny rules are present before any
    /// other rule is removed, so there is no moment with no rules, and rules that already match are never removed and re-added.
    /// </summary>
    // UNVERIFIED: NUC Category=Hyperv run
    public const string Apply = """
        $ErrorActionPreference = 'Stop'
        try {
          $p = $env:BAZAAR_EGRESS_PARAMS | ConvertFrom-Json
          function Get-AclKey($a) {
            $proto = [string]$a.Protocol
            if ($proto -in @('', '*', 'Any')) { $proto = '' }
            $port = [string]$a.RemotePort
            if ($port -in @('', '*', 'Any')) { $port = '' }
            return (@([string]$a.Action, [string]$a.Direction, [string]$a.RemoteIPAddress, $proto, $port, [string][int]$a.Weight, [string][bool]$a.Stateful) -join '|').ToLowerInvariant()
          }
          function Add-Acl($vm, $r) {
            $splat = @{ VMName = $vm; Action = [string]$r.Action; Direction = [string]$r.Direction; RemoteIPAddress = [string]$r.RemoteIPAddress; Weight = [int]$r.Weight; Stateful = [bool]$r.Stateful }
            if ($r.Protocol) { $splat.Protocol = [string]$r.Protocol }
            if ($r.RemotePort) { $splat.RemotePort = [string]$r.RemotePort }
            Add-VMNetworkAdapterExtendedAcl @splat
          }
          $name = [string]$p.VmName
          $want = @($p.Rules | ConvertFrom-Json)
          $wantKeys = @($want | ForEach-Object { Get-AclKey $_ })
          $have = @(Get-VMNetworkAdapterExtendedAcl -VMName $name)
          $haveKeys = @($have | ForEach-Object { Get-AclKey $_ })
          # STEP 1: deny-all rules are added first
          foreach ($r in @($want | Where-Object { $_.Action -eq 'Deny' })) {
            if ((Get-AclKey $r) -notin $haveKeys) { Add-Acl $name $r }
          }
          # STEP 2: rules that are not wanted are removed
          foreach ($a in $have) {
            if ((Get-AclKey $a) -notin $wantKeys) { $a | Remove-VMNetworkAdapterExtendedAcl }
          }
          # STEP 3: wanted allow rules that are missing are added
          foreach ($r in @($want | Where-Object { $_.Action -ne 'Deny' })) {
            if ((Get-AclKey $r) -notin $haveKeys) { Add-Acl $name $r }
          }
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;

    /// <summary>Reads <c>$p.VmName</c> and prints the VM's extended ACLs as one compact JSON array of objects with string fields.</summary>
    // UNVERIFIED: NUC Category=Hyperv run
    public const string ReadBack = """
        $ErrorActionPreference = 'Stop'
        try {
          $p = $env:BAZAAR_EGRESS_PARAMS | ConvertFrom-Json
          $acls = @(Get-VMNetworkAdapterExtendedAcl -VMName ([string]$p.VmName) | ForEach-Object {
            [ordered]@{ Action = [string]$_.Action; Direction = [string]$_.Direction; RemoteIPAddress = [string]$_.RemoteIPAddress; Protocol = [string]$_.Protocol; RemotePort = [string]$_.RemotePort; Weight = [string]$_.Weight; Stateful = [string]$_.Stateful }
          })
          ConvertTo-Json -InputObject $acls -Compress
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;
}
