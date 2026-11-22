namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>
/// The PowerShell scripts, as compile-time constants. They only ever read their data from <c>$p</c> (the parameter JSON in the
/// environment), so no caller value is part of any script text. Every WMI name sits in <see cref="Wmi"/>.
/// </summary>
/// <remarks>
/// UNVERIFIED: spike S-DSP. None of these scripts has run against a live Hyper-V host. The WMI class, method and argument names
/// follow the Microsoft documentation (docs/research/notes/desktop-density/hyperv-and-windows.md) and must be confirmed on the
/// owner's PC before the adapter is relied on.
/// </remarks>
public static class HyperVScripts
{
    /// <summary>Reads <c>$p.Name</c>, <c>$p.Width</c> and <c>$p.Height</c>; prints <c>W H</c> then the raw RGB565 bytes as base64.</summary>
    // UNVERIFIED: spike S-DSP
    public const string Thumbnail = $$"""
        $ErrorActionPreference = 'Stop'
        try {
          {{Wmi.Prelude}}
          $svc = Get-CimInstance -Namespace $ns -ClassName {{Wmi.ManagementService}} | Select-Object -First 1
          $vssd = Get-CimAssociatedInstance -InputObject $vm -Association {{Wmi.RealizedAssociation}} -ResultClassName {{Wmi.SettingData}} | Select-Object -First 1
          if (-not $vssd) { throw 'No realized settings for the VM.' }
          $w = [uint16]$p.Width
          $h = [uint16]$p.Height
          $r = Invoke-CimMethod -InputObject $svc -MethodName {{Wmi.ThumbnailMethod}} -Arguments @{ TargetSystem = $vssd; WidthPixels = $w; HeightPixels = $h }
          if ($r.ReturnValue -eq 4096) {
            $job = $r.Job
            while ($job.JobState -eq 3 -or $job.JobState -eq 4) { Start-Sleep -Milliseconds 100; $job = Get-CimInstance -InputObject $job }
            if ($job.JobState -ne 7) { throw 'The thumbnail job failed.' }
          } elseif ($r.ReturnValue -ne 0) { throw ('Thumbnail failed with code ' + $r.ReturnValue) }
          ('' + $w + ' ' + $h)
          [Convert]::ToBase64String([byte[]]$r.ImageData)
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;

    /// <summary>Reads <c>$p.Name</c> and <c>$p.Steps</c> (a JSON array of <c>{op, text?, code?}</c>) and calls the keyboard methods in order.</summary>
    // UNVERIFIED: spike S-DSP
    public const string Keys = $$"""
        $ErrorActionPreference = 'Stop'
        try {
          {{Wmi.Prelude}}
          $kb = Get-CimInstance -Namespace $ns -ClassName {{Wmi.Keyboard}} | Where-Object { $_.SystemName -eq $vm.Name } | Select-Object -First 1
          if (-not $kb) { throw 'No keyboard for the VM.' }
          $steps = @($p.Steps | ConvertFrom-Json)
          foreach ($s in $steps) {
            if ($s.op -eq 'type') { $r = Invoke-CimMethod -InputObject $kb -MethodName {{Wmi.TypeText}} -Arguments @{ asciiText = [string]$s.text } }
            elseif ($s.op -eq 'key') { $r = Invoke-CimMethod -InputObject $kb -MethodName {{Wmi.TypeKey}} -Arguments @{ keyCode = [uint32]$s.code } }
            elseif ($s.op -eq 'press') { $r = Invoke-CimMethod -InputObject $kb -MethodName {{Wmi.PressKey}} -Arguments @{ keyCode = [uint32]$s.code } }
            elseif ($s.op -eq 'release') { $r = Invoke-CimMethod -InputObject $kb -MethodName {{Wmi.ReleaseKey}} -Arguments @{ keyCode = [uint32]$s.code } }
            else { throw 'Unknown keyboard step.' }
            if ($r.ReturnValue -ne 0) { throw ('Keyboard call failed with code ' + $r.ReturnValue) }
          }
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;

    /// <summary>Reads <c>$p.Name</c> and <c>$p.Steps</c> (a JSON array of <c>{op: move|click|scroll, ...}</c>) and calls the mouse methods in order.</summary>
    // UNVERIFIED: spike S-DSP
    public const string Mouse = $$"""
        $ErrorActionPreference = 'Stop'
        try {
          {{Wmi.Prelude}}
          $mouse = Get-CimInstance -Namespace $ns -ClassName {{Wmi.Mouse}} | Where-Object { $_.SystemName -eq $vm.Name } | Select-Object -First 1
          if (-not $mouse) { throw 'No synthetic mouse for the VM.' }
          $steps = @($p.Steps | ConvertFrom-Json)
          foreach ($s in $steps) {
            if ($s.op -eq 'move') { $r = Invoke-CimMethod -InputObject $mouse -MethodName {{Wmi.SetAbsolutePosition}} -Arguments @{ horizontalPosition = [int]$s.x; verticalPosition = [int]$s.y } }
            elseif ($s.op -eq 'click') { $r = Invoke-CimMethod -InputObject $mouse -MethodName {{Wmi.ClickButton}} -Arguments @{ buttonIndex = [uint32]$s.button } }
            elseif ($s.op -eq 'scroll') { $r = Invoke-CimMethod -InputObject $mouse -MethodName {{Wmi.SetScrollPosition}} -Arguments @{ scrollPositionValue = [int]$s.delta } }
            else { throw 'Unknown mouse step.' }
            if ($r.ReturnValue -ne 0) { throw ('Mouse call failed with code ' + $r.ReturnValue) }
          }
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;

    /// <summary>Every WMI name the scripts use, in one place.</summary>
    private static class Wmi
    {
        public const string Namespace = @"root\virtualization\v2";
        public const string ComputerSystem = "Msvm_ComputerSystem";
        public const string ManagementService = "Msvm_VirtualSystemManagementService";
        public const string SettingData = "Msvm_VirtualSystemSettingData";
        public const string RealizedAssociation = "Msvm_SettingsDefineState";
        public const string Keyboard = "Msvm_Keyboard";
        public const string Mouse = "Msvm_SyntheticMouse";
        public const string ThumbnailMethod = "GetVirtualSystemThumbnailImage";
        public const string TypeText = "TypeText";
        public const string TypeKey = "TypeKey";
        public const string PressKey = "PressKey";
        public const string ReleaseKey = "ReleaseKey";
        public const string SetAbsolutePosition = "SetAbsolutePosition";
        public const string ClickButton = "ClickButton";
        public const string SetScrollPosition = "SetScrollPosition";

        /// <summary>Reads <c>$p</c> and finds the VM by <c>ElementName</c>; the value is compared, never interpolated.</summary>
        public const string Prelude = $$"""
            $p = $env:BAZAAR_DISPLAY_PARAMS | ConvertFrom-Json
              $ns = '{{Namespace}}'
              $vm = Get-CimInstance -Namespace $ns -ClassName {{ComputerSystem}} | Where-Object { $_.ElementName -eq $p.Name -and $_.Caption -eq 'Virtual Machine' } | Select-Object -First 1
              if (-not $vm) { throw 'VM not found.' }
            """;
    }
}
